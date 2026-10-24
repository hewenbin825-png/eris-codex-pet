"""Notification ownership regressions; never launch the pet or create chats."""
from pathlib import Path
from contextlib import closing
import json
import sqlite3
import tempfile
import unittest
from unittest.mock import patch
import notify
import notify_scope as scope

ROOT_ID = '00000000-0000-4000-8000-000000000001'
CHILD_ID = '00000000-0000-4000-8000-000000000002'
GUARD_ID = '00000000-0000-4000-8000-000000000003'
MISSING_ID = '00000000-0000-4000-8000-000000000004'
INDEXED_CHILD_ID = '00000000-0000-4000-8000-000000000005'
CHILD_SOURCE = {'subagent': {'thread_spawn': {'parent_thread_id': ROOT_ID, 'depth': 2}}}


class OwnershipTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='eris-owner-test-')
        self.root = Path(self.temp.name)
        self.home = self.root / 'custom-codex-home'
        self.home.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def database(self, legacy=False):
        with closing(sqlite3.connect(self.home / 'state_5.sqlite')) as db, db:
            db.execute('CREATE TABLE threads (id TEXT PRIMARY KEY, source TEXT' +
                       (')' if legacy else ', agent_path TEXT, thread_source TEXT)'))
            rows = [(ROOT_ID, 'vscode', None, 'user'),
                    (CHILD_ID, json.dumps(CHILD_SOURCE), '/root/worker', 'subagent'),
                    (GUARD_ID, '{"subagent":{"other":"guardian"}}', None, 'guardian_review'),
                    (INDEXED_CHILD_ID, 'vscode', None, 'subagent')]
            db.executemany('INSERT INTO threads VALUES (' + ','.join('?' * (2 if legacy else 4)) + ')',
                           [row[:2] if legacy else row for row in rows])

    def header(self, identifier, metadata, archive=False, recorded_id=None):
        folder = self.home / ('archived_sessions' if archive else 'sessions') / '2026' / '10' / '24'
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / ('rollout-2026-10-24T12-00-00-' + identifier + '.jsonl')
        data = {'type': 'session_meta', 'payload': {'id': recorded_id or identifier, **metadata}}
        path.write_text(json.dumps(data) + '\n' +
                        '{"type":"response_item","payload":{"source":"subagent","text":"PRIVATE_BODY_SENTINEL"}}\n',
                        encoding='utf8')
        return path

    def test_structured_child_markers_win_over_main_hint(self):
        children = [{'source': CHILD_SOURCE}, {'source': json.dumps(CHILD_SOURCE)},
                    {'source': {'subagent': 'review'}}, {'source': 'subagent'},
                    {'thread_source': 'guardian_review'}, {'parent_thread_id': ROOT_ID},
                    {'agent_path': '/root/worker'}, {'agent_id': 'worker'},
                    {'is_subagent': True}]
        for metadata in children:
            with self.subTest(metadata=metadata):
                self.assertEqual(scope.metadata_scope({'source': 'vscode', **metadata}), 'subagent')

    def test_main_fork_and_message_text_are_not_children(self):
        self.assertEqual(scope.metadata_scope({'source': 'vscode', 'forked_from_id': ROOT_ID,
                                               'agent_path': '/root',
                                               'last-assistant-message': 'subagent parent_thread_id /root/worker'}), 'main')
        self.assertEqual(scope.metadata_scope({'source': 'exec'}), 'main')
        self.assertEqual(scope.metadata_scope({'source': 'future-source'}), 'unknown')
        self.assertEqual(scope.metadata_scope({}), 'unknown')

    def test_database_resolves_real_notify_payloads_without_source(self):
        self.database()
        for identifier, expected in ((ROOT_ID, 'main'), (CHILD_ID, 'subagent'), (GUARD_ID, 'subagent'),
                                     (INDEXED_CHILD_ID, 'subagent')):
            with self.subTest(identifier=identifier):
                self.assertEqual(scope.completion_scope({'thread-id': identifier}, self.home),
                                 (expected, 'thread_database'))
        # A generic main hint must not override stored child provenance.
        self.assertEqual(scope.completion_scope({'thread-id': CHILD_ID, 'source': 'cli'}, self.home)[0], 'subagent')

    def test_old_database_schema(self):
        self.database(legacy=True)
        self.assertEqual(scope.completion_scope({'thread-id': CHILD_ID}, self.home)[0], 'subagent')
        self.assertEqual(scope.completion_scope({'thread-id': ROOT_ID}, self.home)[0], 'main')

    def test_rollout_fallback_active_and_archived_without_reading_turns(self):
        self.header(ROOT_ID, {'source': 'vscode'})
        self.header(CHILD_ID, {'source': CHILD_SOURCE}, archive=True)
        self.assertEqual(scope.completion_scope({'thread-id': ROOT_ID}, self.home), ('main', 'session_metadata'))
        self.assertEqual(scope.completion_scope({'thread-id': CHILD_ID}, self.home), ('subagent', 'session_metadata'))
        self.assertFalse(list(self.home.glob('*.sqlite')), 'Reading metadata must not create a database')

    def test_corrupt_or_locked_database_falls_back(self):
        (self.home / 'state_5.sqlite').write_bytes(b'not a database')
        self.header(CHILD_ID, {'source': CHILD_SOURCE})
        self.assertEqual(scope.completion_scope({'thread-id': CHILD_ID}, self.home)[0], 'subagent')
        with patch.object(scope.sqlite3, 'connect', side_effect=sqlite3.OperationalError('database is locked')):
            self.assertEqual(scope.completion_scope({'thread-id': CHILD_ID}, self.home)[0], 'subagent')

    def test_mismatched_or_oversized_header_stays_silent(self):
        path = self.header(MISSING_ID, {'source': 'vscode'}, recorded_id=ROOT_ID)
        self.assertEqual(scope.completion_scope({'thread-id': MISSING_ID}, self.home), ('unknown', 'unresolved'))
        path.write_bytes(b'x' * (scope.HEADER_LIMIT + 1))
        self.assertEqual(scope.completion_scope({'thread-id': MISSING_ID}, self.home)[0], 'unknown')

    def test_unknown_or_invalid_thread_does_not_search_unrelated_files(self):
        with patch.object(scope, 'database_scope') as database, patch.object(scope, 'rollout_scope') as rollout:
            self.assertEqual(scope.completion_scope({'thread-id': '../*'}, self.home), ('unknown', 'unresolved'))
            self.assertEqual(scope.completion_scope({}, self.home), ('unknown', 'unresolved'))
            database.assert_not_called()
            rollout.assert_not_called()

    def test_installation_home_precedes_inherited_home(self):
        with patch.dict('os.environ', {'CODEX_HOME': str(self.root / 'different-home')}):
            self.assertEqual(scope.codex_home({'config_path': str(self.home / 'config.toml')}), self.home.resolve())
            self.assertEqual(scope.codex_home({}), (self.root / 'different-home').resolve())

    def test_entrypoint_suppresses_all_pet_effects_but_forwards_previous_notifier(self):
        self.database()
        pet = self.root / 'pet'
        pet.mkdir()
        previous = ['prior-notifier.exe']
        (pet / 'integration.json').write_text(json.dumps({'config_path': str(self.home / 'config.toml'),
                                                         'previous_notify': previous}), encoding='utf8')
        forwarded = []
        with patch.object(notify, 'ROOT', pet), patch.object(notify, 'current_route', return_value=[]), \
                patch.object(notify.subprocess, 'Popen', side_effect=lambda argv, **kwargs: forwarded.append(argv)), \
                patch.object(notify, 'signal_helper', return_value=0) as signal:
            for identifier in (CHILD_ID, GUARD_ID, MISSING_ID):
                notify.main([json.dumps({'type': 'agent-turn-complete', 'thread-id': identifier,
                                         'turn-id': 'shared-turn', 'last-assistant-message': 'PRIVATE_TEXT_SENTINEL'})])
            signal.assert_not_called()
            self.assertFalse((pet / 'runtime' / 'seen.json').exists())
            main = json.dumps({'type': 'agent-turn-complete', 'thread-id': ROOT_ID, 'turn-id': 'shared-turn'})
            notify.main([main])
            notify.main([main])
            signal.assert_called_once_with()
        self.assertEqual(len(forwarded), 5)
        self.assertTrue(all(argv[0] == previous[0] for argv in forwarded))
        logs = (pet / 'runtime' / 'notify-delivery.jsonl').read_text('utf8')
        rows = [json.loads(line) for line in logs.splitlines()]
        self.assertEqual(sum(row['phase'] == 'completion_suppressed' for row in rows), 3)
        self.assertEqual(sum(row['phase'] == 'helper_signal_delivered' for row in rows), 1)
        self.assertEqual(sum(row['phase'] == 'duplicate_suppressed' for row in rows), 1)
        self.assertNotIn('PRIVATE_TEXT_SENTINEL', logs)
        self.assertNotIn(CHILD_ID, logs)


if __name__ == '__main__':
    unittest.main(verbosity=2)
