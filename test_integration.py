"""Offline tests. No real startup entries, signals, or Codex configuration edits."""
from pathlib import Path
import json,tempfile,tomllib,subprocess
from unittest.mock import patch
import notify as notification
import setup_codex as setup
from notify_route import route_contains,previous_to_run

def tests():
    native=[r'C:\Native App\codex-computer-use.exe','turn-ended']
    own=['pythonw.exe',r'C:\Local Pet\notify.py']
    wrapped=native+['--previous-notify',json.dumps(own)]
    assert route_contains(wrapped,own)
    assert previous_to_run(native,wrapped,own)==[]
    assert previous_to_run(native,own,own)==native
    older=[r'C:\Custom Notifier\alert.exe']
    assert previous_to_run(native+['--previous-notify',json.dumps(older)],wrapped,own)==older
    assert not route_contains(['another.exe',json.dumps(own)],own)
    for text in ('model = "example"\n[features]\na = true\n','notify = ["old.exe"] # keep\nmodel = "example"\n'):
        changed=setup.replace_notify(text,'notify = ["new.exe"]')
        expected=tomllib.loads(text);expected['notify']=['new.exe']
        assert tomllib.loads(changed)==expected
    try:setup.replace_notify('notify = [\n "old.exe",\n]\n','notify = ["new.exe"]')
    except RuntimeError:pass
    else:raise AssertionError('Multiline notify was not rejected')
    with tempfile.TemporaryDirectory(prefix='eris-notify-test-') as folder:
        root=Path(folder)
        (root/'integration.json').write_text(json.dumps({'previous_notify':native,'installed_notify':own}),'utf8')
        payload=json.dumps({'type':'agent-turn-complete','source':'cli','thread-id':'test','turn-id':'1','last-assistant-message':'PRIVATE_TEXT_SENTINEL'})
        calls=[]
        with patch.object(notification,'ROOT',root),patch.object(notification,'current_route',return_value=wrapped),patch.object(notification.subprocess,'Popen',side_effect=lambda argv,**kw:calls.append(argv)),patch.object(notification.subprocess,'run',side_effect=lambda argv,**kw:(calls.append(argv) or subprocess.CompletedProcess(argv,0))):
            notification.main([payload]);notification.main([payload])
            notification.main(['{"type":"approval-requested"}']);notification.main(['invalid-json'])
            assert calls==[[str(root/'ErisVoice.exe'),'--completion']]
        log=(root/'runtime'/'notify-delivery.jsonl').read_text('utf8')
        assert 'PRIVATE_TEXT_SENTINEL' not in log and 'last-assistant-message' not in log
        assert 'duplicate_suppressed' in log
        with patch.object(notification,'ROOT',root),patch.object(notification,'current_route',return_value=wrapped),patch.object(notification,'signal_helper',side_effect=[3,OSError('fixture'),subprocess.TimeoutExpired('fixture',12),0]) as signal:
            retry=json.dumps({'type':'agent-turn-complete','source':'cli','thread-id':'test','turn-id':'retry'})
            for _ in range(5):notification.main([retry])
            assert signal.call_count==4,'Failed signals must remain retryable; success is deduplicated'
        assert (root/'runtime'/'notify-delivery.jsonl').read_text('utf8').count('helper_signal_failed')==3
    with tempfile.TemporaryDirectory(prefix='eris-install-test-') as folder:
        codex=Path(folder);config=codex/'config.toml';target=codex/'pets'/'eris'/'voice'
        before='notify = ["prior.exe", "notify"] # original\nmodel = "example-model"\n[features]\na = true\n'
        config.write_text(before,'utf8');startup_actions=[]
        with patch.object(setup,'CODEX',codex),patch.object(setup,'CONFIG',config),patch.object(setup,'TARGET',target),patch.object(setup,'startup',side_effect=lambda action:startup_actions.append(action)),patch.object(setup,'stop'),patch.object(setup.subprocess,'run'),patch.object(setup.subprocess,'Popen'):
            setup.install()
            current=tomllib.loads(config.read_text('utf8'))
            assert current['model']=='example-model' and current['features']=={'a':True}
            assert (target/'backups').exists() and (target/'audio'/'click_magic_nyan.wav').exists()
            assert (target/'notify_scope.py').is_file()
            assert current['notify'][-1]==str(target/'notify.py')
            try:setup.install()
            except RuntimeError:pass
            else:raise AssertionError('Existing installation was overwritten')
            changed=config.read_text('utf8').replace('example-model','user-updated-model')
            config.write_text(changed,'utf8')
            setup.remove()
            assert config.read_text('utf8')==before.replace('example-model','user-updated-model')
        assert startup_actions==['check','install','check','remove']
    print('PASS: notification routing, deduplication, text exclusion, safe config edit, temp install/remove, preservation of later unrelated edits. External processes and startup changes mocked.')

if __name__=='__main__':tests()
