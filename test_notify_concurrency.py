"""Real Windows process/byte-lock regression; no pet or Codex is launched."""
from pathlib import Path
import json,msvcrt,subprocess,sys,tempfile,time

ROOT=Path(__file__).resolve().parent
WORKER='''
from pathlib import Path
import json,sys,time
sys.path.insert(0,sys.argv[1])
import notify as n
n.ROOT=Path(sys.argv[2])
n.current_route=lambda:[]
def fake_signal():
    with (n.ROOT/'signals.jsonl').open('a') as out:out.write(json.dumps(sys.argv[3])+'\\n')
    time.sleep(.04)
    return 0
n.signal_helper=fake_signal
(n.ROOT/('ready-'+sys.argv[4])).write_text('ready')
n.main([json.dumps({'type':'agent-turn-complete','source':'cli','thread-id':'fixture','turn-id':sys.argv[3],
                    'last-assistant-message':'PRIVATE_TEXT_SENTINEL'})])
'''

def launch(root,turn,worker):
    return subprocess.Popen([sys.executable,'-c',WORKER,str(ROOT),str(root),turn,str(worker)],
                            stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,
                            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))

def finish(children):
    for child in children:
        out,err=child.communicate(timeout=20)
        assert child.returncode==0,(out,err)

def test():
    with tempfile.TemporaryDirectory(prefix='eris-concurrent-') as folder:
        root=Path(folder);(root/'runtime').mkdir()
        (root/'integration.json').write_text('{"previous_notify": [], "installed_notify": []}')
        # Deterministically hit the old read-before-lock bug with an existing byte.
        with (root/'runtime'/'notify.lock').open('w+b') as lock:
            lock.write(b'0');lock.flush();lock.seek(0)
            msvcrt.locking(lock.fileno(),msvcrt.LK_LOCK,1)
            child=launch(root,'held-lock',0)
            try:
                deadline=time.monotonic()+10
                while not (root/'ready-0').exists() and time.monotonic()<deadline:time.sleep(.02)
                assert (root/'ready-0').exists(),'Worker did not become ready'
                time.sleep(.2)
                assert child.poll() is None,'Worker must wait while another process owns the lock'
            finally:
                lock.seek(0);msvcrt.locking(lock.fileno(),msvcrt.LK_UNLCK,1)
        finish([child])
        # A zero-length lock file is valid too: no pre-lock initialization needed.
        (root/'runtime'/'notify.lock').write_bytes(b'')
        finish([launch(root,'same-turn',i+1) for i in range(12)])
        finish([launch(root,'unique-'+str(i),i+20) for i in range(6)])
        signals=[json.loads(line) for line in (root/'signals.jsonl').read_text().splitlines()]
        assert signals.count('held-lock')==1 and signals.count('same-turn')==1 and len(signals)==8,signals
        logs=(root/'runtime'/'notify-delivery.jsonl').read_text('utf8')
        rows=[json.loads(line) for line in logs.splitlines()]
        assert sum(row['phase']=='duplicate_suppressed' for row in rows)==11
        assert sum(row['phase']=='helper_signal_delivered' for row in rows)==8
        assert not any(row['phase'].endswith('error') or row['phase'].endswith('failed') for row in rows)
        assert 'PRIVATE_TEXT_SENTINEL' not in logs
        assert len(json.loads((root/'runtime'/'seen.json').read_text()))==8
    print('PASS: held byte lock waits; empty lock supported; 12 duplicate processes -> 1 signal; 6 unique turns -> 6 signals; no real audio, no private text.')

if __name__=='__main__':test()
