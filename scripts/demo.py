"""HTTP acceptance scenario, stdlib only. Services must already be running."""
import json, time, urllib.request, urllib.error, uuid, sys
from pathlib import Path

tid = sys.argv[1] if len(sys.argv) > 1 else 'demo'
def request(port, path, method='GET', body=None, actor=None, revision=None, key=None):
    headers = {'Content-Type': 'application/json'}
    if actor: headers['Authorization'] = 'Bearer ' + actor
    if revision is not None: headers['If-Match'] = str(revision)
    if method != 'GET': headers['Idempotency-Key'] = key or uuid.uuid4().hex
    data = json.dumps(body).encode() if body is not None else (b'' if method != 'GET' else None)
    req = urllib.request.Request(f'http://localhost:{port}'+path, data=data, method=method, headers=headers)
    with urllib.request.urlopen(req, timeout=15) as response:
        content = response.read()
        return json.loads(content) if content else None

def wait(fn, seconds=40):
    until = time.monotonic()+seconds
    while time.monotonic()<until:
        try:
            value=fn()
            if value: return value
        except (urllib.error.URLError, TimeoutError): pass
        time.sleep(.25)
    raise AssertionError('Timed out waiting for process transition')

for port in (5101,5102,5103,5104): wait(lambda p=port: request(p,'/health'))
base='/tournaments/'+tid
def tournament(): return request(5101,base,actor='organizer-demo')
def command(path, body=None, method='POST', actor='organizer-demo'):
    return request(5101,base+path,method,body,actor,tournament()['revision'])
def expect_error(code, fn):
    try: fn()
    except urllib.error.HTTPError as ex:
        actual=json.loads(ex.read())['code']; assert actual==code,(actual,code); return
    raise AssertionError('Expected rejection: '+code)

try:
    existing=tournament()
except urllib.error.HTTPError as ex:
    if ex.code!=404: raise
    existing=None
if existing: raise SystemExit('Tournament already exists: pass a new ID, e.g. python scripts/demo.py demo2')
seed=json.loads((Path(__file__).parent.parent/'demo/tournament.json').read_text(encoding='utf-8'))
seed['id']=tid
request(5101,'/tournaments','POST',seed,'organizer-demo')
config=json.loads((Path(__file__).parent.parent/'demo/game.json').read_text(encoding='utf-8'))
for game in ('game1','game2'):
    config['id']=game
    for index,task in enumerate(config['tasks']): task['id']=game+'-task'+str(index+1)
    command('/games/'+game,config,'PUT')
command('/publish')
expect_error('two_teams_and_games_required',lambda:command('/start'))
for suffix in ('a','b'):
    captain='captain-'+suffix
    body={'id':suffix,'name':'Team '+suffix.upper(),'captain':captain,'members':[captain,suffix+'1',suffix+'2',suffix+'3']}
    command('/teams',body,actor=captain+'-demo')
command('/start')
expect_error('registration_closed',lambda:command('/teams',{'id':'x','name':'X','captain':'captain-a','members':[]},actor='captain-a-demo'))
for game in ('game1','game2'):
    command('/games/'+game+'/start')
    if game=='game1': expect_error('previous_game_not_completed_or_wrong_order',lambda:command('/games/game2/start'))
    def fixture():
        try: return request(5104,f'/fixtures/{tid}/{game}','POST',actor='simulator-demo')
        except urllib.error.HTTPError as e:
            if e.code>=500 or e.code==409: return None
            raise
    wait(fixture)
    wait(lambda:any(g['config']['id']==game and g['status']=='Completed' for g in tournament()['games']))
    request(5104,f'/fixtures/{tid}/{game}','POST',actor='simulator-demo')
wait(lambda:tournament()['status']=='Completed')
table=wait(lambda:(lambda x:x if x.get('complete') else None)(request(5102,base+'/table')))
assert [(r['id'],r['total']) for r in table['rows']]==[('a',200),('b',196)],table
for port in (5101,5102,5103):
    diag=request(port,'/internal/diagnostics',actor='organizer-demo')
    assert not diag['quarantine'],diag
    assert not diag['pending'],diag
result={'scenario':'2 teams / 2 games; reversed and duplicate events; appeals; sequential games','status':'passed','table':table}
output=Path(__file__).parent.parent/'test-results';output.mkdir(exist_ok=True)
(output/'http-scenario.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print('PASS: complete tournament; A=200, B=196. Table: http://localhost:5102')
