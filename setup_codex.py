"""Optional local Codex notify/startup integration, with backup and removal."""
from pathlib import Path
import ctypes,datetime,json,os,re,shutil,subprocess,sys,tomllib
from notify_route import route_contains
from build import extract_resources

SOURCE=Path(__file__).resolve().parent
CODEX=Path(os.environ.get('CODEX_HOME') or Path.home()/'.codex').expanduser().resolve()
CONFIG=CODEX/'config.toml'
TARGET=CODEX/'pets'/'eris'/'voice'
NO_WINDOW=getattr(subprocess,'CREATE_NO_WINDOW',0)
PATTERN=r'(?m)^notify\s*=\s*\[[^\r\n]*\][ \t]*(?:#[^\r\n]*)?(?=\r?$)'
FILES=['ErisVoice.exe','ErisVoice.cs','ErisCodexStartup.exe','ErisCodexStartup.cs','notify.py','notify_route.py',
       'settings.json','motions.json','setup_codex.py','build.py','resource-manifest.json','resources.zip',
       'README.md','THIRD-PARTY-NOTICES.md','voice-sources.json','motion-references.json']

def replace_notify(text,line):
    old=tomllib.loads(text)
    matches=list(re.finditer(PATTERN,text))
    if len(matches)>1:raise RuntimeError('Multiple notify entries; configuration left untouched')
    if matches:
        match=matches[0]
        changed=text[:match.start()]+line+text[match.end():]
    else:
        if 'notify' in old:raise RuntimeError('Multiline notify requires manual review; configuration left untouched')
        changed=line+'\n'+text if line else text
    return changed

def write_config(text,bom=False):
    temporary=CONFIG.with_name('config.toml.eris.tmp')
    temporary.write_bytes((b'\xef\xbb\xbf' if bom else b'')+text.encode('utf8'))
    os.replace(temporary,CONFIG)

def stop(exe,status_name):
    if not exe.exists():return
    subprocess.run([str(exe),'--stop'],check=True,creationflags=NO_WINDOW)
    try:pid=json.loads((TARGET/'runtime'/status_name).read_text('utf8'))['pid']
    except (ValueError,KeyError,FileNotFoundError):return
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.OpenProcess.argtypes=[ctypes.c_uint32,ctypes.c_int,ctypes.c_uint32];k.OpenProcess.restype=ctypes.c_void_p
    k.QueryFullProcessImageNameW.argtypes=[ctypes.c_void_p,ctypes.c_uint32,ctypes.c_wchar_p,ctypes.POINTER(ctypes.c_uint32)]
    k.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_uint32];k.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=k.OpenProcess(0x100000|0x1000,False,pid)
    if not handle:return
    try:
        buffer=ctypes.create_unicode_buffer(32768);length=ctypes.c_uint32(len(buffer))
        if k.QueryFullProcessImageNameW(handle,0,buffer,ctypes.byref(length)) and Path(buffer.value).resolve()==exe.resolve():
            if k.WaitForSingleObject(handle,5000)!=0:raise RuntimeError('Companion is still running; files left untouched')
    finally:k.CloseHandle(handle)

def startup(action):
    env=dict(os.environ,ERIS_EXE=str(TARGET/'ErisCodexStartup.exe'),ERIS_ACTION=action)
    script=r'''$ErrorActionPreference='Stop'
$path=Join-Path ([Environment]::GetFolderPath('Startup')) 'Eris Codex Voice.lnk'
$wsh=New-Object -ComObject WScript.Shell
if(Test-Path -LiteralPath $path){$existing=$wsh.CreateShortcut($path);if($existing.TargetPath -ne $env:ERIS_EXE){throw 'Startup shortcut belongs to another installation'}}
if($env:ERIS_ACTION -eq 'install'){
 $link=$wsh.CreateShortcut($path);$link.TargetPath=$env:ERIS_EXE;$link.Arguments=''
 $link.WorkingDirectory=Split-Path -Parent $env:ERIS_EXE;$link.WindowStyle=7
 $link.Description='Show Eris while Codex desktop is running';$link.Save()
}elseif($env:ERIS_ACTION -eq 'remove'){
 if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
}
'''
    subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-Command',script],env=env,check=True,creationflags=NO_WINDOW)

def install():
    if TARGET.exists():raise RuntimeError('An Eris installation already exists. It was left untouched. Remove its integration before reinstalling.')
    for name in FILES:
        if not (SOURCE/name).is_file():raise RuntimeError('Missing package file: '+name+'; download the Windows release or build from source')
    pythonw=Path(sys.executable).with_name('pythonw.exe')
    if not pythonw.exists():raise RuntimeError('pythonw.exe is required for quiet notifications')
    extract_resources()
    for name in ('ErisVoice.exe','ErisCodexStartup.exe'):
        subprocess.run([str(SOURCE/name),'--self-test'],check=True,creationflags=NO_WINDOW)
    config_existed=CONFIG.exists();raw=CONFIG.read_bytes() if config_existed else b''
    text=raw.decode('utf-8-sig');old=tomllib.loads(text);previous=old.get('notify',[])
    if not isinstance(previous,list) or not all(isinstance(x,str) for x in previous):raise RuntimeError('Unsupported notify value')
    command=[str(pythonw),str(TARGET/'notify.py')]
    changed=replace_notify(text,'notify = '+json.dumps(command,ensure_ascii=False))
    expected=dict(old);expected['notify']=command
    if tomllib.loads(changed)!=expected:raise RuntimeError('Notification change affected another setting; not installed')
    startup('check')
    TARGET.mkdir(parents=True);(TARGET/'backups').mkdir()
    stamp=datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    (TARGET/'backups'/('config-before-'+stamp+'.toml')).write_bytes(raw)
    for name in FILES:shutil.copy2(SOURCE/name,TARGET/name)
    shutil.copytree(SOURCE/'assets',TARGET/'assets');shutil.copytree(SOURCE/'audio',TARGET/'audio')
    match=re.search(PATTERN,text)
    integration={'config_path':str(CONFIG),'previous_notify':previous,'installed_notify':command,'had_notify':'notify' in old,
                 'previous_notify_line':match.group(0) if match else '', 'config_existed':config_existed,'original_bom':raw.startswith(b'\xef\xbb\xbf')}
    (TARGET/'integration.json').write_text(json.dumps(integration,ensure_ascii=False,indent=2),encoding='utf8')
    config_written=False
    try:
        # Stop an optional portable copy before launching the installed singleton.
        stop(TARGET/'ErisVoice.exe','status.json')
        if (CONFIG.read_bytes() if CONFIG.exists() else b'')!=raw:
            raise RuntimeError('Codex config changed during installation; new config left untouched')
        write_config(changed,integration['original_bom']);config_written=True
        startup('install')
        subprocess.Popen([str(TARGET/'ErisCodexStartup.exe')],creationflags=NO_WINDOW)
    except Exception:
        if config_written and CONFIG.read_text('utf-8-sig')==changed:
            write_config(text,integration['original_bom'])
        raise
    print('Installed to: '+str(TARGET))
    print('Hide the native Codex pet from its menu. Restart Codex after active work finishes to reload notify.')
    print('Automatic completion passed two real desktop turns on the release test machine. Verify one real turn on your own installation.')

def remove():
    info=json.loads((TARGET/'integration.json').read_text('utf8'))
    if Path(info['config_path']).resolve()!=CONFIG:raise RuntimeError('Use the same CODEX_HOME as installation')
    raw=CONFIG.read_bytes();text=raw.decode('utf-8-sig');current=tomllib.loads(text)
    if not route_contains(current.get('notify'),info['installed_notify']):raise RuntimeError('notify changed after installation; left untouched')
    changed=replace_notify(text,info['previous_notify_line'])
    expected=dict(current)
    if info['had_notify']:expected['notify']=info['previous_notify']
    else:expected.pop('notify',None)
    if tomllib.loads(changed)!=expected:raise RuntimeError('Configuration mismatch; left untouched')
    startup('check')
    stop(TARGET/'ErisCodexStartup.exe','autostart-status.json');stop(TARGET/'ErisVoice.exe','status.json')
    startup('remove');write_config(changed,raw.startswith(b'\xef\xbb\xbf'))
    print('Codex integration removed and original notify restored. Program files and backups are kept at: '+str(TARGET))

if __name__=='__main__':
    if os.name!='nt':raise SystemExit('Windows x64 only')
    action=sys.argv[1] if len(sys.argv)==2 else ''
    if action not in ('install','remove'):raise SystemExit('Usage: setup_codex.py install|remove')
    try:(install if action=='install' else remove)()
    except Exception as error:raise SystemExit('Not completed: '+str(error))
