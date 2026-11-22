"""Public API, SSE and anonymity smoke checks; run after demo.py."""
import json, urllib.request, urllib.error, sys, os
base=int(os.environ.get('MATH_BASE_PORT','5101'))
def address(port):return 'http://localhost:'+str(base+port-5101)
tid=sys.argv[1] if len(sys.argv)>1 else 'demo'
for port in (5101,5102,5103):
    with urllib.request.urlopen(address(port)+'/openapi.json',timeout=5) as r:
        assert json.load(r)['openapi']=='3.0.3'
with urllib.request.urlopen(address(5101)+f'/tournaments/{tid}',timeout=5) as r:
    public=json.load(r)
    assert 'reference' not in json.dumps(public).lower()
    assert 'members' not in json.dumps(public).lower()
with urllib.request.urlopen(address(5102)+f'/tournaments/{tid}/stream',timeout=5) as r:
    assert r.headers['Content-Type'].startswith('text/event-stream')
    first=r.readline().decode(); assert first.startswith('data: ')
    table=json.loads(first[6:]);assert table['complete']
    assert 'anonymousId' not in first
with urllib.request.urlopen(address(5102)+'/',timeout=5) as r:
    page=r.read().decode();assert '/app.js' in page and '/styles.css' in page
with urllib.request.urlopen(address(5102)+'/app.js',timeout=5) as r:
    assert 'EventSource' in r.read().decode()
with urllib.request.urlopen(address(5102)+'/styles.css',timeout=5) as r:
    assert '.AppealRejected' in r.read().decode()
try:
    urllib.request.urlopen(urllib.request.Request(address(5102)+'/internal/events',data=b'{}',headers={'Content-Type':'application/json'}),timeout=5)
except urllib.error.HTTPError as ex:
    assert ex.code in (400,403)
else:raise AssertionError('Unauthenticated event was accepted')
print('PASS: OpenAPI, SSE, viewer, public anonymity and event authorization')
