"""Isolated process-level lab acceptance, including a real coordinator kill/restart."""
import importlib.util,json,os,subprocess,time,urllib.request,urllib.error,uuid
from pathlib import Path
root=Path(__file__).resolve().parent.parent;folder=root/'test-results'/('lab-process-'+uuid.uuid4().hex[:8]);folder.mkdir(parents=True)
spec=importlib.util.spec_from_file_location('lab_demo',root/'scripts/lab-demo.py');lab=importlib.util.module_from_spec(spec);spec.loader.exec_module(lab);lab.base=7201
processes={};logs=[]
def launch(name,index,enabled=True):
    env=os.environ.copy();env.update(SERVICE=name,DB=str(folder/(name+'.db')),ENABLE_LAB_DEMOS='1' if enabled else '0',ASPNETCORE_URLS=f'http://127.0.0.1:{lab.base+index}',SCORES_URL=f'http://127.0.0.1:{lab.base+1}')
    for i,dest in enumerate(['tournament','scores','results','simulator']):env['PEER_'+dest.upper()]=f'http://127.0.0.1:{lab.base+i}'
    exe=root/('src/Simulator/bin/Debug/net8.0/Simulator.dll' if name=='simulator' else 'src/Host/bin/Debug/net8.0/Host.dll')
    log=open(folder/(name+'-'+str(time.time_ns())+'.log'),'w',encoding='utf-8');logs.append(log)
    kwargs={'creationflags':subprocess.CREATE_NO_WINDOW} if os.name=='nt' else {}
    processes[name]=subprocess.Popen(['dotnet',str(exe)],cwd=root,env=env,stdout=log,stderr=subprocess.STDOUT,**kwargs)
    for _ in range(100):
        try:
            if lab.api(index,'/health',token=None)['status']=='ok':return
        except (urllib.error.URLError,TimeoutError):pass
        time.sleep(.1)
    raise AssertionError(name+' did not start')
def restart():
    processes['tournament'].kill();processes['tournament'].wait();launch('tournament',0)
    print('Saga coordinator process killed and restored from its own SQLite',flush=True)
try:
    for i,name in enumerate(['tournament','scores','results','simulator']):launch(name,i)
    result=[lab.saga(restart),lab.cap()]
    for name,index,path,token in [('tournament',0,'/lab/delivery/scores/pause','organizer-demo'),('simulator',3,'/lab/reject-next-forms','simulator-demo')]:
        processes[name].kill();processes[name].wait();launch(name,index,False)
        try:lab.api(index,path,'POST',token=token);raise AssertionError('Lab control enabled in normal environment')
        except urllib.error.HTTPError as error:assert error.code==404
    result.append({'scenario':'disabled-lab-controls','status':'passed'})
    (root/'test-results/lab-process-checks.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print('PASS isolated process-level lab checks',flush=True)
finally:
    for p in processes.values():
        if p.poll() is None:p.terminate()
    for p in processes.values():
        try:p.wait(timeout=5)
        except subprocess.TimeoutExpired:p.kill()
    for log in logs:log.close()
