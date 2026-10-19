"""Chain Codex's original notifier and signal local Eris audio. No network access."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys
import msvcrt
import datetime
import tomllib
import time
import errno
from contextlib import contextmanager
from notify_route import previous_to_run

ROOT = Path(__file__).resolve().parent
NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)

@contextmanager
def delivery_lock(runtime, timeout=15):
    # Windows byte-range locks also forbid reads by other processes. Never read
    # or initialize this byte before locking it. Locking beyond EOF is supported.
    with (runtime/'notify.lock').open('a+b') as lock:
        deadline=time.monotonic()+timeout
        while True:
            lock.seek(0)
            try:
                msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
                break
            except OSError as error:
                if error.errno not in (errno.EACCES,errno.EAGAIN,errno.EDEADLK) or time.monotonic()>=deadline:
                    raise
                time.sleep(.05)
        try:yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(),msvcrt.LK_UNLCK,1)

def signal_helper():
    # Exit zero acknowledges delivery to the helper, not audible playback.
    return subprocess.run([str(ROOT/'ErisVoice.exe'),'--completion'],
                          stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,
                          stderr=subprocess.DEVNULL,creationflags=NO_WINDOW,
                          timeout=12).returncode

def audit(phase, **fields):
    """Record delivery metadata only, never notification text or raw arguments."""
    try:
        runtime=ROOT/'runtime';runtime.mkdir(exist_ok=True)
        path=runtime/'notify-delivery.jsonl'
        if path.exists() and path.stat().st_size>131072:
            path.replace(runtime/('notify-delivery-'+datetime.datetime.now().strftime('%Y%m%d%H%M%S%f')+'.jsonl'))
        row={'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'phase':phase,**fields}
        with path.open('a',encoding='utf8') as out:out.write(json.dumps(row,ensure_ascii=False)+'\n')
    except OSError:pass

def current_route():
    try:
        info=json.loads((ROOT/'integration.json').read_text('utf8'))
        config=Path(info.get('config_path',str(Path.home()/'.codex'/'config.toml')))
        return tomllib.loads(config.read_text(encoding='utf-8-sig')).get('notify',[])
    except (OSError,ValueError):return []

def completed(payload):
    try:
        return json.loads(payload).get('type') == 'agent-turn-complete'
    except (ValueError, AttributeError):
        return False

def main(args=None):
    args = sys.argv[1:] if args is None else args
    if not args:
        return 0
    accepted=completed(args[-1])
    audit('received',supported_completion=accepted)
    cfg = json.loads((ROOT / 'integration.json').read_text(encoding='utf-8'))
    previous = cfg.get('previous_notify', [])
    previous=previous_to_run(previous,current_route(),cfg.get('installed_notify',[]))
    if previous:
        try:
            subprocess.Popen([*previous, *args], stdin=subprocess.DEVNULL,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             creationflags=NO_WINDOW, close_fds=True)
        except OSError as error:
            audit('previous_notifier_error',error_type=type(error).__name__)
    if not accepted:
        return 0
    message = json.loads(args[-1])
    turn = message.get('turn-id')
    runtime = ROOT / 'runtime'
    runtime.mkdir(exist_ok=True)
    with delivery_lock(runtime):
        digest=None
        if turn:
            digest=hashlib.sha256((str(message.get('thread-id',''))+':'+str(turn)).encode()).hexdigest()
            path=runtime/'seen.json'
            try:
                seen=json.loads(path.read_text())
                if not isinstance(seen,list) or not all(isinstance(x,str) for x in seen):seen=[]
            except (FileNotFoundError,ValueError):seen=[]
            if digest in seen:
                audit('duplicate_suppressed',turn_hash=digest)
                return 0
        try:code=signal_helper()
        except (OSError,subprocess.TimeoutExpired) as error:
            audit('helper_signal_failed',turn_hash=digest,error_type=type(error).__name__)
            return 0
        if code!=0:
            audit('helper_signal_failed',turn_hash=digest,returncode=code)
            return 0
        if turn:
            temp=runtime/'seen.json.tmp'
            temp.write_text(json.dumps([*seen[-63:],digest]),encoding='utf8')
            os.replace(temp,path)
        audit('helper_signal_delivered',turn_hash=digest,returncode=code)
    return 0

if __name__ == '__main__':
    try: sys.exit(main())
    except Exception as error:
        audit('delivery_error',error_type=type(error).__name__,errno=getattr(error,'errno',None))
        sys.exit(0)
