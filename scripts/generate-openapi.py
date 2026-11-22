"""Generate checked-in OpenAPI 3.0 contract. No runtime dependencies."""
import json
from pathlib import Path
def obj(properties, required=None): return {'type':'object','properties':properties,'required':required or list(properties)}
def string(): return {'type':'string'}
def arr(item): return {'type':'array','items':item}
def ref(name): return {'$ref':'#/components/schemas/'+name}
schemas={
 'TaskRule':obj({'id':string(),'statement':string(),'reference':string(),'correct':{'type':'number'},'incorrect':{'type':'number'}}),
 'GameConfig':obj({'id':string(),'type':string(),'tasks':arr(ref('TaskRule')),'modifiers':{'type':'object','additionalProperties':{'type':'number'}},'solutionSeconds':{'type':'integer','minimum':1},'appealSeconds':{'type':'integer','minimum':1}}),
 'Team':obj({'id':string(),'name':string(),'captain':string(),'members':{'type':'array','minItems':4,'maxItems':4,'uniqueItems':True,'items':string()}}),
 'Tournament':obj({'id':string(),'name':string(),'starts':{'type':'string','format':'date-time'},'ends':{'type':'string','format':'date-time'},'teamSize':{'type':'integer','enum':[4]},'jury':arr(string()),'senior':string(),'regulation':string(),'tiePolicy':{'type':'string','enum':['SharedCompetition']}}),
 'Binding':obj({'anonymousId':string(),'teamId':string(),'taskId':string(),'formType':string()}),
 'Verdict':obj({'anonymousId':string(),'revision':{'type':'integer','enum':[1,2]},'outcome':{'type':'string','enum':['Correct','Incorrect','AppealUpheld','AppealRejected']},'juryId':string(),'authority':{'type':'string','enum':['Jury','Senior']}}),
 'TeamSeal':obj({'anonymousIds':arr(string())}), 'CheckSeal':obj({'verdictEventIds':arr(string())}),
 'AnonymousReview':obj({'anonymousId':string(),'taskId':string(),'photoUrl':string(),'statement':string(),'reference':string(),'assignedJury':string(),'appeal':{'type':'boolean'},'reason':{'type':'string','nullable':True}}),
 'Standing':obj({'teamId':string(),'score':{'type':'number'},'place':{'type':'integer'},'points':{'type':'integer'}}),
 'ScoreSnapshot':obj({'rows':arr(ref('Standing')),'appliedVerdicts':arr(string())}),
 'Event':obj({'id':string(),'version':{'type':'integer','enum':[1]},'type':string(),'producer':{'type':'string','enum':['tournament','scores','results','team','checking']},'tournamentId':string(),'gameId':{'type':'string','nullable':True},'occurredAt':{'type':'string','format':'date-time'},'correlationId':string(),'causationId':{'type':'string','nullable':True},'data':{'type':'object'}}),
 'Error':obj({'code':string()})}
paths={}
def operation(path,method,summary,body=None,command=False,auth=False,response=None):
    parameters=[{'in':'path','name':part[1:-1],'required':True,'schema':string()} for part in path.split('/') if part.startswith('{')]
    if command:
        parameters += [{'in':'header','name':'Idempotency-Key','required':True,'schema':string()}]
        if path!='/tournaments': parameters += [{'in':'header','name':'If-Match','required':True,'schema':{'type':'integer'}}]
    responses={code:{'description':desc,'content':{'application/json':{'schema':ref('Error')}}} for code,desc in [('400','Invalid request'),('403','Forbidden'),('404','Not found'),('409','Domain constraint or concurrency conflict'),('428','If-Match required')]}
    responses['202' if path=='/internal/events' else '200']={'description':'Success','content':{('text/event-stream' if path.endswith('/stream') else 'application/json'):{'schema':response or {'type':'object'}}}}
    value={'summary':summary,'parameters':parameters,'responses':responses}
    if body:value['requestBody']={'required':True,'content':{'application/json':{'schema':ref(body)}}}
    if auth:value['security']=[{'bearer':[]}]
    paths.setdefault(path,{})[method]=value
operation('/health','get','Health')
operation('/openapi.json','get','OpenAPI contract')
operation('/internal/events','post','Durably accept producer-authenticated event','Event',auth=True)
operation('/internal/diagnostics','get','Organizer: pending inbox, quarantine and outbox',auth=True)
operation('/tournaments','post','Tournament service: create','Tournament',True,True)
operation('/tournaments','get','Tournament service: published tournaments and organizer-owned drafts',response=arr({'type':'object'}))
operation('/tournaments/{id}','get','Tournament service: public regulation; organizer sees configuration')
operation('/tournaments/{id}/games/{game}','put','Tournament service: add/update game','GameConfig',True,True)
operation('/tournaments/{id}/games/{game}','delete','Tournament service: delete configured game',command=True,auth=True)
operation('/tournaments/{id}/teams','post','Tournament service: captain registers four members','Team',True,True)
for suffix in ('publish','start','games/{game}/start'):operation('/tournaments/{id}/'+suffix,'post','Tournament service: '+suffix,command=True,auth=True)
operation('/tournaments/{id}/table','get','Scores/results service: live table and final standings')
operation('/tournaments/{id}/stream','get','Scores/results service: SSE snapshots, reconnect fetches latest state')
operation('/ui-api/fixtures/{id}/{game}','post','UI: organizer invokes test simulator for the selected game',auth=True)
document={'openapi':'3.0.3','info':{'title':'Math Tournament services','version':'1.0.0'},'servers':[{'url':f'http://localhost:{port}','description':name} for port,name in [(5101,'Tournament'),(5102,'Scores'),(5103,'Results')]],'paths':paths,'components':{'schemas':schemas,'securitySchemes':{'bearer':{'type':'http','scheme':'bearer'}}}}
destination=Path(__file__).parent.parent/'docs/openapi.json'
destination.write_text(json.dumps(document,ensure_ascii=False,indent=2),encoding='utf-8')
