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
from notify_route import previous_to_run

ROOT = Path(__file__).resolve().parent
NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)

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
    with (runtime / 'notify.lock').open('a+b') as lock:
        lock.seek(0)
        if not lock.read(1):
            lock.write(b'0'); lock.flush()
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
        try:
            if turn:
                digest = hashlib.sha256((str(message.get('thread-id', '')) + ':' + str(turn)).encode()).hexdigest()
                path = runtime / 'seen.json'
                try: seen = json.loads(path.read_text())
                except (FileNotFoundError, ValueError): seen = []
                if digest in seen:
                    audit('duplicate_suppressed',turn_hash=digest)
                    return 0
            subprocess.Popen([str(ROOT / 'ErisVoice.exe'), '--completion'],
                             stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, creationflags=NO_WINDOW)
            if turn:path.write_text(json.dumps([*seen[-63:], digest]))
            audit('helper_signal_spawned',turn_hash=digest if turn else None)
        finally:
            lock.seek(0); msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
    return 0

if __name__ == '__main__':
    try: sys.exit(main())
    except Exception as error:
        audit('delivery_error',error_type=type(error).__name__)
        sys.exit(0)
