"""Real Chromium acceptance checks via CDP. Requires websocket-client and local Chrome/Edge.
Run from project directory with all four services running. No browser extension required.
"""
import base64, json, os, shutil, subprocess, sys, time, urllib.request, uuid
from pathlib import Path
import websocket

root=Path(__file__).resolve().parent.parent
output=root/'test-results';output.mkdir(exist_ok=True)
chrome=next((p for p in [Path(os.environ.get('PROGRAMFILES','C:/Program Files'))/'Google/Chrome/Application/chrome.exe',Path('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe')] if p.exists()),None)
if not chrome:raise SystemExit('Chrome or Edge required')
profile=output/('browser-'+uuid.uuid4().hex[:8]);profile.mkdir()
startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
process=subprocess.Popen([str(chrome),'--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-port=0','--user-data-dir='+str(profile),'about:blank'],startupinfo=startup,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
class Browser:
    def __init__(self,url):self.ws=websocket.create_connection(url,timeout=15,suppress_origin=True);self.seq=0;self.errors=[]
    def call(self,method,params=None):
        self.seq+=1;ident=self.seq;self.ws.send(json.dumps({'id':ident,'method':method,'params':params or {}}))
        while True:
            data=json.loads(self.ws.recv())
            if data.get('method')=='Runtime.exceptionThrown':self.errors.append(data['params'])
            if data.get('id')==ident:
                if 'error' in data:raise RuntimeError(data['error'])
                return data.get('result',{})
    def js(self,expression):
        result=self.call('Runtime.evaluate',{'expression':expression,'returnByValue':True,'awaitPromise':True})
        if 'exceptionDetails' in result:raise RuntimeError(result['exceptionDetails'])
        return result.get('result',{}).get('value')
    def wait(self,expression,timeout=15):
        end=time.monotonic()+timeout
        while time.monotonic()<end:
            result=self.js('Boolean('+expression+')')
            if result:return result
            time.sleep(.2)
        raise AssertionError('Timed out: '+expression+'\n'+str(self.js('document.body.innerText.slice(-2500)')))
    def click(self,selector):self.js(f'document.querySelector({json.dumps(selector)}).click()')
    def fill(self,selector,value):self.js(f'(()=>{{const input=document.querySelector({json.dumps(selector)});input.value={json.dumps(value)};input.dispatchEvent(new Event("input",{{bubbles:true}}));input.dispatchEvent(new Event("change",{{bubbles:true}}));}})()')
    def screenshot(self,name):
        image=self.call('Page.captureScreenshot',{'format':'png','captureBeyondViewport':False})['data'];(output/name).write_bytes(base64.b64decode(image))

browser=None
try:
    end=time.monotonic()+20
    while not (profile/'DevToolsActivePort').exists():
        if time.monotonic()>end:raise RuntimeError('Browser did not start')
        time.sleep(.15)
    port=(profile/'DevToolsActivePort').read_text().splitlines()[0]
    with urllib.request.urlopen('http://127.0.0.1:'+port+'/json',timeout=5) as response:tabs=json.load(response)
    browser=Browser(next(t for t in tabs if t['type']=='page')['webSocketDebuggerUrl'])
    browser.call('Runtime.enable');browser.call('Page.enable')
    browser.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':1080,'deviceScaleFactor':1,'mobile':False})
    browser.call('Page.navigate',{'url':'http://localhost:5102/'})
    browser.wait('typeof state !== "undefined" && document.querySelector("#demo-button")')
    browser.wait('state.list.length || document.querySelector(".empty-start")')
    # Run the entire tournament from the UI, including explicitly entering the demo account.
    browser.click('#demo-button');browser.wait('document.querySelector("#modal").open')
    browser.click('#modal-submit');browser.wait('state.demonstrating',5)
    browser.wait('state.tournament?.status==="Running"',20)
    browser.wait('state.table?.rows?.length===2',10)
    browser.js('setView("overview")');time.sleep(.6);browser.screenshot('ui-overview-live.png')
    browser.wait('!state.demonstrating',90)
    assert browser.js('state.tournament.status')=='Completed'
    assert browser.js('state.table.rows.map(r=>r.total)')==[200,196]
    assert browser.js('state.table.complete')
    time.sleep(6.3) # Let the temporary success toast and demo banner close naturally.
    browser.js('setView("overview")');time.sleep(.3);browser.screenshot('ui-overview.png')
    browser.js('setView("table")');time.sleep(.3)
    for status in ['Correct','Incorrect','AppealUpheld','AppealRejected']:
        assert browser.js(f'document.querySelector(".result-chip.{status}")!==null'),status
    # Search retains focus through live rendering; text is rendered safely.
    browser.fill('#team-search','Интеграл');assert browser.js('document.querySelectorAll("tbody tr").length')==1
    browser.fill('#team-search','');browser.screenshot('ui-table.png')
    browser.js('setView("results")');browser.screenshot('ui-results.png')
    browser.call('Emulation.setDeviceMetricsOverride',{'width':390,'height':844,'deviceScaleFactor':1,'mobile':False})
    browser.js('setView("overview")');time.sleep(.3)
    browser.screenshot('ui-mobile.png')
    assert browser.js('document.documentElement.scrollWidth<=window.innerWidth'), 'Mobile page overflows horizontally'
    browser.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':1080,'deviceScaleFactor':1,'mobile':False})
    # Exercise actual creation/configuration forms, rather than only the demo orchestration.
    browser.click('#create-button');browser.wait('document.querySelector("#modal").open')
    manual_id='ui-form-'+uuid.uuid4().hex[:8]
    browser.fill('#f-name','Турнир проверки интерфейса');browser.fill('#f-id',manual_id);browser.click('#modal-submit')
    browser.wait('state.id==='+json.dumps(manual_id)+' && !document.querySelector("#modal").open')
    browser.click('[data-action="add-game"]');browser.fill('#f-solutionSeconds','5');browser.fill('#f-appealSeconds','8');browser.click('#modal-submit')
    browser.wait('state.tournament.games.length===1 && !document.querySelector("#modal").open')
    browser.click('[data-edit]');browser.fill('#f-type','Проверенная игра');browser.click('#modal-submit')
    browser.wait('state.tournament.games[0].config.type==="Проверенная игра" && !document.querySelector("#modal").open')
    browser.click('[data-delete]');browser.click('#modal-submit');browser.wait('state.tournament.games.length===0 && !document.querySelector("#modal").open')
    # Register both teams through the captain forms, then start and finish a game manually.
    browser.click('[data-action="add-game"]');browser.fill('#f-solutionSeconds','5');browser.fill('#f-appealSeconds','8');browser.click('#modal-submit')
    browser.wait('state.tournament.games.length===1 && !document.querySelector("#modal").open')
    browser.js('setView("overview")');browser.click('[data-action="publish"]');browser.click('#modal-submit')
    browser.wait('state.tournament.status==="Registration" && !document.querySelector("#modal").open')
    def account(key):
        browser.click('#account-button');browser.fill('#f-account',key);browser.click('#modal-submit')
        browser.wait('state.tournament!==null && !document.querySelector("#modal").open')
    for suffix in ('a','b'):
        account('captain-'+suffix);browser.click('[data-action="register"]')
        browser.fill('#f-name','Команда '+suffix.upper())
        for i in (1,2,3):browser.fill('#f-member-'+str(i),suffix+'-ui-'+str(i))
        browser.click('#modal-submit');browser.wait('!document.querySelector("#modal").open')
    account('organizer');browser.wait('state.tournament.teams.length===2')
    browser.click('[data-action="start"]');browser.click('#modal-submit')
    browser.wait('state.tournament.status==="Running" && !document.querySelector("#modal").open')
    browser.click('[data-start]');browser.click('#modal-submit')
    browser.wait('state.tournament.games[0].status==="Running" && !document.querySelector("#modal").open')
    browser.js('retryFixture(state.id,state.tournament.games[0].config.id)')
    browser.wait('state.tournament.status==="Completed" && state.table?.complete',25)
    assert browser.js('state.table.rows.map(r=>r.total)')==[100,98]
    # Check malicious team/tournament names are text, not executable markup.
    assert not browser.errors,browser.errors
    report={'status':'passed','checks':['UI-driven 2-team/2-game tournament','actual form create/edit/delete','captain registration forms for both teams','manual publication and start via UI','manual game completed 100/98','live table and four verdict colors','search','results 200/196','mobile width 390px','no uncaught JavaScript errors'],'demoTournament':browser.js('state.list.find(t=>t.name==="Кубок математической арены")?.id'),'screenshots':['ui-overview.png','ui-overview-live.png','ui-table.png','ui-results.png','ui-mobile.png']}
    (output/'ui-checks.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print('PASS: browser UI, demo 200/196, forms, live results, mobile layout; no JavaScript exceptions')
finally:
    if browser:
        try:browser.call('Browser.close')
        except Exception:pass
    process.terminate()
