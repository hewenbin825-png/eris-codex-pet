"""Build locally using Windows .NET Framework; no dependency download."""
from pathlib import Path
import hashlib,json,os,subprocess,zipfile

ROOT=Path(__file__).resolve().parent
NO_WINDOW=getattr(subprocess,'CREATE_NO_WINDOW',0)

def extract_resources(root=ROOT):
    manifest=json.loads((root/'resource-manifest.json').read_text('utf8'))
    with zipfile.ZipFile(root/'resources.zip') as archive:
        if set(archive.namelist())!=set(manifest):raise RuntimeError('Unexpected resource archive contents')
        for name,digest in manifest.items():
            destination=(root/name).resolve()
            if not destination.is_relative_to(root.resolve()):raise RuntimeError('Invalid resource path')
            data=archive.read(name)
            if hashlib.sha256(data).hexdigest()!=digest:raise RuntimeError('Resource hash mismatch: '+name)
            if destination.exists():
                if hashlib.sha256(destination.read_bytes()).hexdigest()!=digest:raise RuntimeError('Existing resource changed; not overwritten: '+name)
            else:
                destination.parent.mkdir(parents=True,exist_ok=True);destination.write_bytes(data)

def build():
    if os.name!='nt':raise RuntimeError('This release targets Windows x64')
    extract_resources()
    compiler=Path(os.environ.get('WINDIR',r'C:\Windows'))/'Microsoft.NET'/'Framework64'/'v4.0.30319'/'csc.exe'
    if not compiler.exists():raise RuntimeError('.NET Framework 4.x compiler is missing')
    for name,references in [('ErisVoice',['System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll']),('ErisCodexStartup',['System.Web.Extensions.dll'])]:
        sources=[name+'.cs']
        if name=='ErisVoice':sources+=['ErisUsage.cs','ErisPanels.cs','ErisSkin.cs','ErisPanelTests.cs']
        args=[str(compiler),'/nologo','/target:winexe','/platform:x64','/optimize+','/debug-',
              *['/r:'+x for x in references],'/out:'+str(ROOT/(name+'.exe')),*[str(ROOT/s) for s in sources]]
        subprocess.run(args,check=True,creationflags=NO_WINDOW)
        subprocess.run([str(ROOT/(name+'.exe')),'--self-test'],check=True,creationflags=NO_WINDOW)
    print('Build and self-tests passed. Run Start.cmd.')

if __name__=='__main__':build()
