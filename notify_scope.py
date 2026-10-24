"""Classify completion ownership from metadata, without reading chat messages."""
from pathlib import Path
import json
import os
import re
import sqlite3
import uuid

MAIN_SOURCES = {'cli', 'exec', 'vscode', 'desktop', 'app-server', 'app_server', 'appserver'}
SUBAGENT_SOURCES = {'subagent', 'sub_agent', 'sub-agent', 'guardian_review'}
HEADER_LIMIT = 1024 * 1024


def metadata_scope(metadata):
    """Child markers take precedence; missing information is not proof of a root."""
    if not isinstance(metadata, dict):
        return 'unknown'
    for key in ('parent_thread_id', 'parent-thread-id', 'parentThreadId',
                'agent_id', 'agent-id', 'agent_type', 'agent-type'):
        if metadata.get(key):
            return 'subagent'
    if metadata.get('is_subagent') is True or metadata.get('isSubagent') is True:
        return 'subagent'
    agent_path = metadata.get('agent_path', metadata.get('agentPath'))
    if isinstance(agent_path, str) and agent_path.strip() not in ('', '/root', '/root/'):
        return 'subagent'
    thread_source = metadata.get('thread_source', metadata.get('threadSource'))
    if isinstance(thread_source, str) and thread_source.lower() in SUBAGENT_SOURCES:
        return 'subagent'
    main = thread_source == 'user'
    for key in ('source', 'session_source', 'sessionSource'):
        source = metadata.get(key)
        if isinstance(source, str):
            try:
                decoded = json.loads(source)
                if isinstance(decoded, (dict, str)):
                    source = decoded
            except ValueError:
                pass
        if isinstance(source, dict):
            if 'subagent' in source or 'sub_agent' in source:
                return 'subagent'
        elif isinstance(source, str):
            if source.lower() in SUBAGENT_SOURCES:
                return 'subagent'
            main = main or source.lower() in MAIN_SOURCES
    return 'main' if main else 'unknown'


def codex_home(integration):
    """Use the installation's home, including installations with CODEX_HOME."""
    config = integration.get('config_path')
    if isinstance(config, str) and config:
        return Path(config).expanduser().resolve().parent
    return Path(os.environ.get('CODEX_HOME') or Path.home() / '.codex').expanduser().resolve()


def thread_id(message):
    value = message.get('thread-id', message.get('thread_id', message.get('session_id')))
    try:
        return str(uuid.UUID(value)) if isinstance(value, str) else None
    except (ValueError, AttributeError):
        return None


def database_scope(home, identifier):
    """Read only ownership columns. Do not create a database or read its text."""
    try:
        candidates = []
        for path in home.glob('state_*.sqlite'):
            match = re.fullmatch(r'state_(\d+)\.sqlite', path.name)
            if match:
                candidates.append((int(match.group(1)), path))
        for _, path in sorted(candidates, reverse=True)[:3]:
            try:
                db = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=.25)
                try:
                    db.execute('PRAGMA query_only=ON')
                    columns = {row[1] for row in db.execute('PRAGMA table_info(threads)')}
                    fields = [key for key in ('source', 'agent_path', 'thread_source', 'parent_thread_id')
                              if key in columns]
                    if 'id' not in columns or not fields:
                        continue
                    row = db.execute('SELECT ' + ','.join(fields) + ' FROM threads WHERE id=?',
                                     (identifier,)).fetchone()
                    if row is not None:
                        scope = metadata_scope(dict(zip(fields, row)))
                        if scope != 'unknown':
                            return scope
                finally:
                    db.close()
            except (OSError, sqlite3.Error, ValueError):
                continue
    except OSError:
        pass
    return 'unknown'


def rollout_scope(home, identifier):
    """Fallback to the matching session's first metadata line, never its turns."""
    for name in ('sessions', 'archived_sessions'):
        try:
            for path in (home / name).rglob('rollout-*-' + identifier + '.jsonl'):
                try:
                    resolved = path.resolve()
                    if not resolved.is_relative_to((home / name).resolve()):
                        continue
                    with resolved.open('rb') as stream:
                        line = stream.readline(HEADER_LIMIT + 1)
                    if len(line) > HEADER_LIMIT:
                        continue
                    row = json.loads(line)
                    metadata = row.get('payload') if isinstance(row, dict) else None
                    if (row.get('type') != 'session_meta' or not isinstance(metadata, dict)
                            or metadata.get('id') != identifier):
                        continue
                    scope = metadata_scope(metadata)
                    if scope != 'unknown':
                        return scope
                except (OSError, ValueError, AttributeError):
                    continue
        except OSError:
            continue
    return 'unknown'


def completion_scope(message, home):
    """Return (main/subagent/unknown, evidence kind). Unknown stays silent."""
    hint = metadata_scope(message)
    if hint == 'subagent':
        return hint, 'event_metadata'
    identifier = thread_id(message)
    if identifier:
        for lookup, reason in ((database_scope, 'thread_database'),
                               (rollout_scope, 'session_metadata')):
            scope = lookup(Path(home), identifier)
            if scope != 'unknown':
                return scope, reason
    return hint, 'event_metadata' if hint == 'main' else 'unresolved'
