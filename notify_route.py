"""Understand the native desktop notifier's existing forwarding envelope."""
import json,re

def native_wrapper(command):
    return (isinstance(command,list) and len(command)>=2 and command[1]=='turn-ended'
            and isinstance(command[0],str)
            and bool(re.fullmatch(r'codex-computer-use(?:-arm64)?\.exe',command[0].replace('\\','/').split('/')[-1],re.I)))

def forwarded(command):
    if not native_wrapper(command):return None
    try:
        i=command.index('--previous-notify')
        value=json.loads(command[i+1])
        return value if isinstance(value,list) and all(isinstance(x,str) for x in value) else None
    except (ValueError,IndexError,TypeError):return None

def route_contains(current,expected):
    if not expected:return False
    for _ in range(8):
        if current==expected:return True
        current=forwarded(current)
        if current is None:return False
    return False

def previous_to_run(previous,current,own):
    # The desktop app can wrap our notify command on restart. In that case its
    # own turn-ended handler already ran; preserve only an older nested notifier.
    if current!=own and route_contains(current,own) and native_wrapper(previous):
        for _ in range(8):
            if not native_wrapper(previous):return previous
            previous=forwarded(previous)
            if previous is None:return []
        return []
    return previous
