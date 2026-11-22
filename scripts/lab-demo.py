"""Reproducible Saga compensation and CAP/cache demonstrations against live services.
Use --base-port 7101 when start.ps1 was launched with -BasePort 7101.
"""
import argparse,json,time,urllib.request,urllib.error,uuid
from datetime import datetime,timezone,timedelta
from pathlib import Path
base=5101
def api(service,path,method='GET',body=None,token='organizer-demo',revision=None):
    headers={'Content-Type':'application/json','Idempotency-Key':uuid.uuid4().hex}
    if token:headers['Authorization']='Bearer '+token
    if revision is not None:headers['If-Match']=str(revision)
    data=json.dumps(body).encode() if body is not None else b'' if method!='GET' else None
    req=urllib.request.Request(f'http://127.0.0.1:{base+service}'+path,data=data,headers=headers,method=method)
    with urllib.request.urlopen(req,timeout=10) as r:
        content=r.read();return json.loads(content) if content else None
def wait(fn,timeout=25):
    end=time.monotonic()+timeout
    while time.monotonic()<end:
        value=fn()
        if value:return value
        time.sleep(.12)
    raise AssertionError('Timeout waiting for distributed state')
def tournament(id):return api(0,'/tournaments/'+id)
def cmd(id,path,body=None,token='organizer-demo',method='POST'):
    return api(0,'/tournaments/'+id+path,method,body,token,tournament(id)['revision'])
def pause(service,destination,enabled):api(service,'/lab/delivery/'+destination+('/pause' if enabled else '/resume'),'POST',token='simulator-demo' if service==3 else 'organizer-demo')
def diag(service):return api(service,'/internal/diagnostics')
def create(prefix):
    id=prefix+'-'+uuid.uuid4().hex[:8];now=datetime.now(timezone.utc)
    api(0,'/tournaments','POST',{'id':id,'name':'Laboratory '+prefix,'starts':now.isoformat(),'ends':(now+timedelta(days=1)).isoformat(),'teamSize':4,'jury':['j','s'],'senior':'s','tiePolicy':'SharedCompetition','regulation':'correct 10 * modifier; shared competition ranking'})
    for i in (1,2):cmd(id,'/games/g'+str(i),{'id':'g'+str(i),'type':'Math','tasks':[{'id':f'g{i}q','statement':'2+2','reference':'4','correct':10,'incorrect':0}],'modifiers':{'normal':1},'solutionSeconds':2,'appealSeconds':4},method='PUT')
    cmd(id,'/publish')
    for suffix in ('a','b'):cmd(id,'/teams',{'id':suffix,'name':suffix,'captain':'captain-'+suffix,'members':['captain-'+suffix,suffix+'1',suffix+'2',suffix+'3']},token='captain-'+suffix+'-demo')
    cmd(id,'/start');return id
def game(id,index=0):return tournament(id)['games'][index]
def running(id,index=0):return wait(lambda:game(id,index) if game(id,index)['status']=='Running' else None)
def fixture(id,index=0):
    gid='g'+str(index+1)
    def attempt():
        try:return api(3,f'/fixtures/{id}/{gid}','POST',token='simulator-demo')
        except urllib.error.HTTPError as e:
            if e.code in (409,500):return None
            raise
    wait(attempt)
def envelope(id,kind,producer,attempt):
    return {'id':uuid.uuid4().hex,'version':1,'type':kind,'producer':producer,'tournamentId':id,'gameId':'g1','occurredAt':datetime.now(timezone.utc).isoformat(),'correlationId':id,'causationId':None,'data':{'attemptId':attempt}}
def saga(restart=None):
    id=create('saga');pause(3,'tournament',True)
    try:
        api(3,'/lab/reject-next-forms','POST',token='simulator-demo');cmd(id,'/games/g1/start')
        attempt=game(id)['preparationAttemptId']
        wait(lambda:any(r['attemptId']==attempt and r['status']=='Rejected' for r in api(3,'/lab/reservations',token='simulator-demo')['reservations']))
        assert any(r['attemptId']==attempt and r['status']=='Reserved' for r in diag(1)['reservations'])
        pause(1,'tournament',True)
        if restart:restart()
        pause(3,'tournament',False)
        wait(lambda:game(id)['status']=='Compensating')
        assert game(id)['preparationFailure']=='demo_forms_rejected'
        wait(lambda:any(r['attemptId']==attempt and r['status']=='Released' for r in diag(1)['reservations']))
        assert game(id)['status']=='Compensating'
        # Repeat release is safe, and late success cannot reverse the compensation decision.
        api(1,'/internal/events','POST',envelope(id,'ReleaseScoreboard','tournament',attempt),'tournament-integration-demo')
        api(0,'/internal/events','POST',envelope(id,'FormsReserved','simulator',attempt),'simulator-integration-demo')
        assert game(id)['status']=='Compensating'
        print('Saga: first reservation really Released; game waits for last compensation acknowledgement',flush=True)
        pause(1,'tournament',False);wait(lambda:game(id)['status']=='Configured')
        cmd(id,'/games/g1/start');new_attempt=game(id)['preparationAttemptId'];assert new_attempt!=attempt
        api(0,'/internal/events','POST',envelope(id,'FormsReserved','simulator',attempt),'simulator-integration-demo')
        running(id);assert game(id)['preparationAttemptId']==new_attempt
        fixture(id);wait(lambda:game(id)['status']=='Completed')
        cmd(id,'/games/g2/start');running(id,1);fixture(id,1);wait(lambda:tournament(id)['status']=='Completed')
        print('PASS Saga: compensation, duplicate release, late old reply, retry and tournament completion',flush=True)
        return {'scenario':'saga','status':'passed','tournament':id,'oldAttempt':attempt,'retryAttempt':new_attempt,'coordinatorRestarted':restart is not None}
    finally:pause(3,'tournament',False);pause(1,'tournament',False)
def cap():
    id=create('cap');cmd(id,'/games/g1/start');running(id)
    pause(0,'scores',True);pause(2,'tournament',True)
    try:
        fixture(id);wait(lambda:game(id)['solutionsClosed'] and game(id)['appealsClosed'])
        table=api(1,f'/tournaments/{id}/table');assert not table['complete'];assert diag(0)['outboxPending']>0
        assert table==api(1,f'/tournaments/{id}/table')
        print('CAP: score reads stay available; deadline closures persisted despite paused delivery',flush=True)
        pause(0,'scores',False)
        wait(lambda:api(1,f'/tournaments/{id}/table')['rows'][0]['games'][0]['final'] is not None)
        try:cmd(id,'/games/g2/start');raise AssertionError('Next game started before confirmed final')
        except urllib.error.HTTPError as e:assert e.code==409
        pause(2,'tournament',False);wait(lambda:game(id)['status']=='Completed')
        cmd(id,'/games/g2/start');running(id,1);fixture(id,1);wait(lambda:tournament(id)['status']=='Completed')
        final=wait(lambda:(lambda t:t if t['complete'] else None)(api(1,f'/tournaments/{id}/table')))
        assert [r['total'] for r in final['rows']]==[200,196]
        before=diag(1)['cache']
        for _ in range(10):assert api(1,f'/tournaments/{id}/table')==final
        with urllib.request.urlopen(f'http://127.0.0.1:{base+1}/tournaments/{id}/stream',timeout=5) as response:
            assert json.loads(response.readline().decode()[6:])==final
        after=diag(1)['cache'];assert after['hits']>before['hits']
        print('PASS CAP/cache: confirmed-final gate, eventual convergence 200/196, shared GET/SSE snapshot, cache hits',flush=True)
        return {'scenario':'cap-cache','status':'passed','tournament':id,'points':[200,196],'cacheBefore':before,'cacheAfter':after}
    finally:pause(0,'scores',False);pause(2,'tournament',False)
if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('scenario',choices=['saga','cap','all'],default='all',nargs='?');parser.add_argument('--base-port',type=int,default=5101);args=parser.parse_args();base=args.base_port
    results=[]
    if args.scenario in ('saga','all'):results.append(saga())
    if args.scenario in ('cap','all'):results.append(cap())
    out=Path(__file__).resolve().parent.parent/'test-results';out.mkdir(exist_ok=True);(out/'lab-scenarios.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
