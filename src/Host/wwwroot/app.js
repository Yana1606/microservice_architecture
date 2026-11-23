'use strict';
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const icon = name => `<svg aria-hidden="true"><use href="#i-${name}"/></svg>`;
const number = value => Number(value || 0).toLocaleString('ru-RU', { maximumFractionDigits: 2 });
const uid = () => crypto.randomUUID();
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const statusNames = {Preparing:'Подготовка игры',Compensating:'Выполняется отмена',Compensated:'Резервы освобождены',Succeeded:'Игра запущена',Draft:'Черновик',Registration:'Регистрация открыта',Running:'Турнир идёт',Completed:'Турнир завершён',Configured:'Ожидает старта'};
const cellNames = {NotSubmitted:'Не сдано',Correct:'Верно',Incorrect:'Неверно',AppealUpheld:'Апелляция удовлетворена',AppealRejected:'Апелляция отклонена'};
const viewNames = {overview:['Обзор турнира','Всё, что происходит на арене, — в одном месте.'],teams:['Команды турнира','Четыре участника. Одна команда. Общая цель.'],games:['Игры турнира','От первой задачи до последнего вердикта.'],table:['Онлайн-таблица','Баллы, места и апелляции — в реальном времени.'],results:['Итоги турнира','Каждая игра имеет значение.']};
const errors = {demo_forms_rejected:'Имитатор отклонил резервирование бланков.',preparation_timeout:'Истёк срок подготовки игры. Ожидается освобождение резервов.',revision_conflict:'Данные изменились. Обновите страницу и повторите действие.',registration_closed:'Регистрация уже закрыта.',organizer_required:'Действие доступно только организатору этого турнира.',authentication_or_role_required:'Выберите подходящую учётную запись или укажите действительный токен.',captain_required:'Зарегистрировать команду может её капитан.',exactly_four_distinct_members:'В команде должно быть ровно четыре разных участника, включая капитана.',team_or_member_already_registered:'Команда или один из участников уже зарегистрированы.',two_teams_and_games_required:'Для старта нужны минимум две команды и хотя бы одна игра.',previous_game_not_completed_or_wrong_order:'Сначала завершите предыдущую игру. Игры запускаются по порядку.',tournament_already_started:'После начала турнира изменить игры нельзя.',not_ready_to_publish:'Добавьте хотя бы одну игру и заполните регламент.',invalid_game:'Проверьте задачи, множители и сроки игры. Срок апелляций не может быть меньше срока решений.',invalid_tournament:'Проверьте название, даты и состав жюри. Старший жюри должен входить в состав.',tournament_exists:'Этот идентификатор турнира уже занят.',tournament_not_found:'Турнир не найден. Проверьте выбранный идентификатор.',tournament_service_unavailable:'Сервис турнира недоступен. Проверьте, что все процессы запущены.',simulator_unavailable:'Имитатор недоступен. Для демонстрации запустите четвёртый процесс.',wait_for_game_started:'Имитатор ещё получает событие начала игры. Повторите через несколько секунд.'};
const accounts = {
 viewer: {name:'Зритель',role:'viewer',label:'Просмотр результатов',token:'',initial:'З'},
 organizer: {name:'Организатор',role:'organizer',label:'Демонстрационная учётная запись',token:'organizer-demo',initial:'О'},
 'captain-a': {name:'Капитан A',role:'captain',label:'Демонстрационная учётная запись',token:'captain-a-demo',initial:'A'},
 'captain-b': {name:'Капитан B',role:'captain',label:'Демонстрационная учётная запись',token:'captain-b-demo',initial:'B'}
};
let account = accounts.viewer;
try { const saved = JSON.parse(sessionStorage.getItem('arena-account') || 'null'); if (saved?.role && typeof saved.token === 'string') account = saved; } catch { /* Ignore expired browser preferences. */ }
const state = {id:new URLSearchParams(location.search).get('tournament') || localStorage.getItem('arena-tournament') || '',tournament:null,table:null,list:[],view:viewNames[location.hash.slice(1)]?location.hash.slice(1):'overview',game:'',search:'',busy:false,demonstrating:false};
let stream, streamTimer, toastTimer, refreshing, modalHandler, modalBusy=false;
function badge(status, game=false) { return `<span class="badge ${String(status).toLowerCase()}">${escapeHtml(game&&status==='Running'?'Игра идёт':statusNames[status]||status)}</span>`; }
function gameConfig(game) { return game.config || {id:game.id,type:game.type,tasks:(state.table?.games?.find(x=>x.id===game.id)?.tasks||[]).map(id=>({id}))}; }
function gameName(game,index) { const type=gameConfig(game).type; return type && type!=='DemoMath' ? type : `Игра ${index+1}`; }
function dateLabel(value) { return value ? new Date(value).toLocaleDateString('ru-RU',{day:'numeric',month:'long',year:'numeric'}) : 'Дата не задана'; }
function setAccount(next) { account=next; sessionStorage.setItem('arena-account',JSON.stringify(next));updateAccount(); }
function updateAccount() {
 $('#account-name').textContent=account.name;$('#account-role').textContent=account.label;$('.account .avatar').textContent=account.initial;
 $('#create-button').hidden=account.role==='captain';
}
function notify(message,error=false) { clearTimeout(toastTimer);const node=$('#toast');node.textContent=message;node.classList.toggle('error',error);node.hidden=false;toastTimer=setTimeout(()=>node.hidden=true,error?8500:4500); }
async function api(path,{method='GET',body,token=account.token,revision}={}) {
 const headers={};if(token)headers.Authorization='Bearer '+token;
 if(method!=='GET')headers['Idempotency-Key']=uid();
 if(revision!==undefined)headers['If-Match']=String(revision);
 if(body!==undefined)headers['Content-Type']='application/json';
 let response;
 try {response=await fetch(path,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});}catch{throw new Error('Нет соединения с сервисом. Проверьте запуск приложения.');}
 const text=await response.text();let data;try{data=text?JSON.parse(text):null;}catch{data=null;}
 if(!response.ok){const code=data?.code;const error=new Error(errors[code]||`Не удалось выполнить действие (${response.status}).`);error.code=code;error.status=response.status;throw error;}
 return data;
}
const tournamentPath = (suffix='',id=state.id) => '/ui-api/tournament/tournaments/'+encodeURIComponent(id)+suffix;
async function command(suffix,body,method='POST',token=account.token,id=state.id) {
 const current=await api(tournamentPath('',id),{token});
 return api(tournamentPath(suffix,id),{method,body,token,revision:current.revision});
}
function owner() { return account.role==='organizer' && !!state.tournament?.organizer; }
function editable() { return owner()&&['Draft','Registration'].includes(state.tournament?.status); }
function setView(view) { state.view=view;history.replaceState(null,'','#'+view);render();window.scrollTo({top:0,behavior:'smooth'}); }
async function loadList() {
 const actor=account;
 const list=await api('/ui-api/tournament/tournaments',{token:actor.token});
 if(account!==actor)return;
 state.list=list;
 const select=$('#tournament-select');select.replaceChildren(new Option('Выберите турнир',''));
 state.list.forEach(t=>select.add(new Option(t.name,t.id)));
 if(state.id&&!state.list.some(x=>x.id===state.id))select.add(new Option(state.id,state.id));
 select.value=state.id;
}
async function selectTournament(id) {
 state.id=id;state.tournament=null;state.table=null;state.game='';state.search='';
 localStorage.setItem('arena-tournament',id);stream?.close();clearTimeout(streamTimer);
 render();await refresh();connectStream();
}
async function refresh({quiet=false}={}) {
 const actor=account;
 if(refreshing?.id===state.id&&refreshing.actor===actor)return;
 const request={id:state.id,actor};
 refreshing=request;
 const isCurrent=()=>refreshing===request&&state.id===request.id&&account===actor;
 try {
  if(!request.id){
   await loadList();
   if(!isCurrent())return;
   const first=state.list.find(t=>t.status==='Running')||state.list[0];
   if(first){
    request.id=state.id=first.id;
    localStorage.setItem('arena-tournament',first.id);
    $('#tournament-select').value=first.id;
    connectStream();
   }
  }
  if(request.id){
   const previousTable=state.table;
   const [t,table]=await Promise.all([
    api(tournamentPath('',request.id),{token:actor.token}),
    api('/tournaments/'+encodeURIComponent(request.id)+'/table')
   ]);
   if(!isCurrent())return;
   // An SSE snapshot received during the request takes precedence.
   const nextTable=state.table===previousTable?table:state.table;
   const changed=JSON.stringify(t)!==JSON.stringify(state.tournament)||JSON.stringify(nextTable)!==JSON.stringify(state.table);
   state.tournament=t;state.table=nextTable;
   if(changed)render();
  }
  $('#connection').className='connection online';$('#connection').lastChild.textContent='Сервисы на связи';
 } catch(error) {
  if(!isCurrent())return;
  $('#connection').className='connection offline';$('#connection').lastChild.textContent='Нет соединения';
  if(!quiet)notify(error.message,true);
  if(!state.tournament)render(error.message);
 } finally {
  if(refreshing===request)refreshing=undefined;
 }
}
function connectStream() {
 stream?.close();clearTimeout(streamTimer);if(!state.id)return;
 const id=state.id;stream=new EventSource('/tournaments/'+encodeURIComponent(id)+'/stream');
 stream.onmessage=event=>{if(state.id!==id)return;try{const table=JSON.parse(event.data);if(JSON.stringify(table)!==JSON.stringify(state.table)){state.table=table;render();}$('#connection').className='connection online';$('#connection').lastChild.textContent='Обновляется онлайн';}catch{/* Next snapshot will recover the view. */}};
 stream.onerror=()=>{$('#connection').className='connection offline';$('#connection').lastChild.textContent='Восстанавливаем связь';};
}
function hero() {
 const t=state.tournament;if(!t)return '';
 let actions='';
 if(owner()&&t.status==='Draft')actions=`<button class="button primary" data-action="publish">${icon('check')}Открыть регистрацию</button>`;
 if(owner()&&t.status==='Registration')actions=`<button class="button primary" data-action="start" ${t.teams.length<2?'disabled title="Нужно минимум две команды"':''}>${icon('play')}Начать турнир</button>`;
 if(account.role==='captain'&&t.status==='Registration')actions=`<button class="button primary" data-action="register">${icon('plus')}Зарегистрировать команду</button>`;
 if(t.status==='Completed')actions=`<button class="button secondary" data-view="results">${icon('cup')}Посмотреть итоги</button>`;
 return `<section class="hero"><div class="hero-copy"><div class="eyebrow">ТУРНИР МАТЕМАТИЧЕСКИХ ИГР</div><h2>${escapeHtml(t.name)}</h2><div class="hero-meta"><span>${icon('clock')}${escapeHtml(dateLabel(t.starts))}</span><span>${icon('users')}Команды по 4 человека</span>${badge(t.status)}</div>${actions?`<div class="hero-actions">${actions}</div>`:''}</div><div class="hero-art" aria-hidden="true"><div class="orbit"></div><span class="sigma">∑</span><span class="formula">a² + b² = c²</span><span class="pi">π</span></div></section>`;
}
function metrics() {
 const t=state.tournament;const games=t.games||[];const done=games.filter(g=>g.status==='Completed').length;const active=games.findIndex(g=>g.status==='Running');
 const cards=[['Команд на арене',t.teams.length,`${t.teams.length*4} участников`,'users'],['Игр в турнире',games.length,`${done} из ${games.length} завершено`,'game'],['Текущая игра',active>=0?String(active+1).padStart(2,'0'):done===games.length&&done?'Финиш':'—',active>=0?'Идёт приём и проверка':t.status==='Completed'?'Все результаты учтены':'Ожидаем начала игры','clock'],['На кону за победу','100','Турнирных очков за игру','cup']];
 return `<div class="metric-grid">${cards.map(([label,value,note,i])=>`<div class="metric"><div class="metric-icon">${icon(i)}</div><div class="metric-label">${label}</div><div class="metric-value">${value}</div><div class="metric-note">${note}</div></div>`).join('')}</div>`;
}
function emptyTable(message='Таблица появится после начала турнира.') {return `<div class="empty">${icon('chart')}<h3>Арена ждёт участников</h3><p>${message}</p></div>`;}
function leaderboard(compact=false) {
 const data=state.table;
 if(!data?.games||!data.rows?.length)return `<section class="panel"><div class="panel-head"><h2>${icon('chart')}Онлайн-таблица</h2><span class="live-small">LIVE</span></div>${emptyTable()}</section>`;
 if(!data.games.some(g=>g.id===state.game))state.game=data.games[0]?.id||'';
 const selected=data.games.find(g=>g.id===state.game);const tasks=selected?.tasks||[];
 const rows=data.rows.filter(r=>r.name.toLocaleLowerCase('ru').includes(state.search.toLocaleLowerCase('ru')));
 const shortRows=compact?rows.slice(0,5):rows;
 const head=`<thead><tr><th>Место</th><th class="team-col">Команда</th>${tasks.map((_,i)=>`<th>Задача ${i+1}</th>`).join('')}<th>Баллы игры</th><th>Место в игре</th><th>Очки игры</th><th>Сумма</th></tr></thead>`;
 const body=shortRows.map((team,index)=>{const game=team.games.find(g=>g.id===state.game);return `<tr><td><span class="place ${team.place===1?'first':''}">${team.place}</span></td><td class="team-col"><div class="team-cell"><span class="team-emblem tone-${index%4}">${escapeHtml(initials(team.name))}</span>${escapeHtml(team.name)}</div></td>${tasks.map(task=>{const c=game?.cells?.[task];const status=Object.hasOwn(cellNames,c?.status)?c.status:'NotSubmitted';return `<td class="task-cell"><span class="result-chip ${status}" title="${cellNames[status]}" aria-label="${cellNames[status]}: ${number(c?.points)}">${c&&status!=='NotSubmitted'?number(c.points):'—'}</span></td>`;}).join('')}<td class="score-value">${game?.score!==null&&game?.score!==undefined?number(game.score):game?.final?number(game.final.score):'—'}</td><td>${game?.final?.place??game?.place??'—'}</td><td class="score-value">${game?.final?.points??'—'}</td><td class="total-value">${number(team.total)}</td></tr>`;}).join('');
 return `<section class="panel"><div class="panel-head"><div><h2>${icon('chart')}${compact?'Онлайн-таблица':'Турнирная таблица'}<span class="live-small">LIVE</span></h2><p>Результаты обновляются автоматически</p></div>${compact?'<button class="text-button" data-view="table">Все результаты</button>':`<button class="button secondary small-button" data-action="export">${icon('download')}Скачать CSV</button>`}</div><div class="table-toolbar"><div class="tabs">${data.games.map((g,i)=>`<button class="tab ${state.game===g.id?'active':''}" data-game="${escapeHtml(g.id)}">Игра ${i+1}</button>`).join('')}</div>${!compact?`<label class="search">${icon('search')}<input id="team-search" placeholder="Найти команду" aria-label="Поиск команды" value="${escapeHtml(state.search)}"></label>`:''}</div><div class="table-scroll"><table>${head}<tbody>${body||`<tr><td colspan="${tasks.length+6}">Команды не найдены</td></tr>`}</tbody></table></div><div class="legend"><span><i class="Correct"></i>Верно</span><span><i class="Incorrect"></i>Неверно</span><span><i class="AppealUpheld"></i>Апелляция удовлетворена</span><span><i class="AppealRejected"></i>Отклонена</span></div></section>`;
}
function initials(name) {return name.trim().split(/\s+/).map(x=>x[0]).slice(0,2).join('').toUpperCase();}
function schedule() {
 const games=state.tournament.games;
 return `<section class="panel"><div class="panel-head"><h2>${icon('game')}Игры турнира</h2><button class="text-button" data-view="games">Все игры</button></div><div class="panel-body">${games.length?games.map((g,i)=>`<div class="mini-game"><span class="game-num ${g.status==='Completed'?'done':''}">${g.status==='Completed'?icon('check'):String(i+1).padStart(2,'0')}</span><div><strong>${escapeHtml(gameName(g,i))}</strong><p>${gameConfig(g).tasks?.length||state.table?.games?.find(x=>x.id===gameConfig(g).id)?.tasks.length||'—'} задачи</p>${badge(g.status,true)}</div></div>`).join(''):'<p class="form-note">Игры пока не настроены.</p>'}</div></section>`;
}
function regulation() {return `<section class="panel regulation"><div class="panel-head"><h2>Коротко о правилах</h2></div><div class="panel-body"><p>${escapeHtml(state.tournament.regulation||'Организатор ещё не опубликовал регламент.')}</p><div class="rule-line"><span>Размер команды</span><strong>4 участника</strong></div><div class="rule-line"><span>Очки за место</span><strong>100 − 2 × (место − 1)</strong></div></div></section>`;}
function currentGame() {
 const t=state.tournament;const games=t.games;const running=games.find(g=>g.status==='Running');const next=games.find(g=>g.status!=='Completed');const selected=running||next;
 if(!selected)return `<section class="panel current-game"><div class="panel-body">${icon('cup')}<h3>${t.status==='Completed'?'Победители определены':'Все игры сыграны'}</h3><p>${t.status==='Completed'?'Итоги доступны всем участникам.':'Ожидаем подтверждения итогов.'}</p><button class="button secondary" data-view="results">Итоги турнира</button></div></section>`;
 const i=games.indexOf(selected);const due=selected.solutionsClosed?selected.appealsDue:selected.solutionsDue;
 let action='';
 if(owner()&&t.status==='Running'&&selected.status==='Configured')action=`<button class="button primary" data-start="${escapeHtml(gameConfig(selected).id)}">${icon('play')}Начать игру</button>`;
 return `<section class="panel current-game"><div class="panel-body"><div class="eyebrow">${running?'СЕЙЧАС НА АРЕНЕ':'СЛЕДУЮЩАЯ ИГРА'}</div><h3>${escapeHtml(gameName(selected,i))}</h3>${running?`<div class="countdown" data-deadline="${escapeHtml(due||'')}">${remaining(due)}</div><p>${selected.appealsClosed?'Приёмы закрыты. Завершаются проверки.':selected.solutionsClosed?'До закрытия апелляций':'До закрытия приёма решений'}</p>`:'<p>Начнётся после завершения предыдущих игр.</p>'}${badge(selected.status,true)}${action}</div></section>`;
}
function remaining(due){if(!due)return '—';let seconds=Math.max(0,Math.ceil((new Date(due).getTime()-Date.now())/1000));return `${String(Math.floor(seconds/60)).padStart(2,'0')}:${String(seconds%60).padStart(2,'0')}`;}
function overview(){return hero()+metrics()+`<div class="columns"><div>${leaderboard(true)}${schedule()}</div><div class="aside-panels">${currentGame()}${regulation()}</div></div>`;}
function teamsView(){
 const t=state.tournament;
 return `<section class="panel"><div class="panel-head"><div><h2>${icon('users')}Составы команд</h2><p>${t.teams.length} команд · ${t.teams.length*4} участников</p></div>${account.role==='captain'&&t.status==='Registration'?`<button class="button primary small-button" data-action="register">${icon('plus')}Зарегистрировать команду</button>`:badge(t.status)}</div></section>${t.teams.length?`<div class="team-grid">${t.teams.map((team,i)=>`<article class="team-card"><span class="team-emblem tone-${i%4}">${escapeHtml(initials(team.name))}</span><h3>${escapeHtml(team.name)}</h3><p>4 участника · ${escapeHtml(team.id)}</p>${team.members?team.members.map(m=>`<div class="member"><span class="member-mark">${escapeHtml(m[0]?.toUpperCase())}</span>${escapeHtml(m)}${m===team.captain?'<small>Капитан</small>':''}</div>`).join(''):'<div class="about-status">Личные данные состава доступны организатору.</div>'}</article>`).join('')}</div>`:`<section class="panel">${emptyTable(t.status==='Registration'?'Регистрация открыта. Капитаны могут заявить команды.':'После открытия регистрации здесь появятся команды.')}</section>`}`;
}
function gamesView(){
 const t=state.tournament;const next=t.games.find(g=>g.status!=='Completed');
 return `<section class="panel"><div class="panel-head"><div><h2>${icon('game')}Программа турнира</h2><p>Игры проходят последовательно. Следующая доступна после подведения итогов предыдущей.</p></div>${owner()&&state.labEnabled?`<button class="button secondary small-button" data-action="reject-forms">Учебный отказ бланков</button>`:""}${editable()?`<button class="button primary small-button" data-action="add-game">${icon('plus')}Добавить игру</button>`:''}</div></section>${t.games.length?t.games.map((g,i)=>{const cfg=gameConfig(g);return `<article class="game-card"><span class="game-num ${g.status==='Completed'?'done':''}">${g.status==='Completed'?icon('check'):String(i+1).padStart(2,'0')}</span><div><h3>${escapeHtml(gameName(g,i))}</h3><p>${escapeHtml(cfg.id)}</p>${g.preparationAttemptId?`<div class="about-status">Подготовка: ${escapeHtml(statusNames[g.preparationStatus]||g.preparationStatus||g.status)} · попытка ${escapeHtml(g.preparationAttemptId.slice(0,8))}${g.preparationFailure?`<br>${escapeHtml(errors[g.preparationFailure]||g.preparationFailure)}`:""}</div>`:""}<div class="game-tags"><span>${icon('grid')}${cfg.tasks?.length||'—'} задачи</span>${cfg.solutionSeconds?`<span>${icon('clock')}Решения: ${cfg.solutionSeconds} с</span><span>Апелляции: ${cfg.appealSeconds} с</span>`:''}</div></div><div class="game-actions">${badge(g.status,true)}${editable()?`<button class="button secondary small-button" data-edit="${escapeHtml(cfg.id)}" title="Изменить игру">${icon('edit')}</button><button class="button danger small-button" data-delete="${escapeHtml(cfg.id)}" title="Удалить игру">Удалить</button>`:''}${owner()&&t.status==='Running'&&g===next&&g.status==='Configured'?`<button class="button primary small-button" data-start="${escapeHtml(cfg.id)}">${icon('play')}Начать</button>`:''}</div></article>`;}).join(''):`<section class="panel"><div class="empty">${icon('game')}<h3>Составьте программу турнира</h3><p>Добавьте задачи, эталоны, баллы и сроки. До начала турнира игры можно изменять.</p></div></section>`}`;
}
function resultsView(){
 const rows=state.table?.rows||[];const complete=state.table?.complete;
 if(!rows.length)return `<section class="panel">${emptyTable('Итоги появятся после проведения игр.')}</section>`;
 const winners=rows.filter(r=>r.place===1);
 return `<div class="about-status">${complete?`${winners.length>1?'Совместные победители определены.':'Победитель определён.'} Все игры завершены, очки учтены.`:'Промежуточный зачёт. Победитель будет определён после завершения всех игр.'}</div>${complete?`<div class="podium">${rows.slice(0,3).map(row=>`<article class="podium-card ${row.place===1?'winner':''}">${icon('cup')}<div class="eyebrow">${row.place===1?'ПОБЕДИТЕЛЬ ТУРНИРА':row.place+' МЕСТО'}</div><h3>${escapeHtml(row.name)}</h3><div class="podium-score">${number(row.total)} <small>очков</small></div><p>${state.tournament.games.length} игры в общем зачёте</p></article>`).join('')}</div>`:''}<section class="panel"><div class="panel-head"><h2>${icon('cup')}Общий зачёт</h2><button class="button secondary small-button" data-action="export">${icon('download')}Скачать CSV</button></div><div class="table-scroll"><table><thead><tr><th>Место</th><th class="team-col">Команда</th>${state.tournament.games.map((_,i)=>`<th>Игра ${i+1}</th>`).join('')}<th>Сумма очков</th></tr></thead><tbody>${rows.map((row,i)=>`<tr><td><span class="place ${row.place===1?'first':''}">${row.place}</span></td><td class="team-col"><div class="team-cell"><span class="team-emblem tone-${i%4}">${escapeHtml(initials(row.name))}</span>${escapeHtml(row.name)}</div></td>${row.games.map(g=>`<td>${g.final?.points??'—'}</td>`).join('')}<td class="total-value">${number(row.total)}</td></tr>`).join('')}</tbody></table></div></section>`;
}
function render(error=''){
 const [title,subtitle]=viewNames[state.view];$('#page-title').textContent=title;$('#page-subtitle').textContent=subtitle;
 $$('.nav-item').forEach(b=>{b.classList.toggle('active',b.dataset.view===state.view);b.setAttribute('aria-current',b.dataset.view===state.view?'page':'false');});
 $('#breadcrumb').textContent=state.tournament?.name||'Математическая арена';$('#team-count').textContent=state.tournament?.teams?.length||0;updateAccount();
 if(!state.tournament){$('#content').innerHTML=`${error?`<div class="inline-error">${escapeHtml(error)}</div>`:''}<section class="empty-start"><span class="math-large" aria-hidden="true">∑</span><h2>Здесь начинается игра</h2><p>Создайте турнир, пригласите команды и наблюдайте, как решения превращаются в победы. Для знакомства запустите демонстрационный турнир.</p><button class="button primary" data-action="demo">${icon('play')}Попробовать демонстрацию</button></section>`;return;}
 const focused=document.activeElement?.id==='team-search';const cursor=focused?document.activeElement.selectionStart:null;
 const views={overview,teams:teamsView,games:gamesView,table:()=>metrics()+leaderboard(),results:resultsView};$('#content').innerHTML=views[state.view]();
 if(focused&&$('#team-search')){$('#team-search').focus();$('#team-search').setSelectionRange(cursor,cursor);}
}
function showModal(title,html,handler,{submit='Сохранить',eyebrow='НАСТРОЙКА ТУРНИРА'}={}){
 $('#modal-title').textContent=title;$('#modal-eyebrow').textContent=eyebrow;$('#modal-body').innerHTML=html;$('#form-error').textContent='';$('#modal-submit').textContent=submit;$('#modal-submit').disabled=false;modalHandler=handler;modalBusy=false;$('#modal').showModal();
}
function closeModal(){if(!modalBusy)$('#modal').close();}
function field(label,name,value='',type='text',extra=''){return `<div class="field"><label for="f-${escapeHtml(name)}">${escapeHtml(label)}</label><input id="f-${escapeHtml(name)}" name="${escapeHtml(name)}" value="${escapeHtml(value)}" type="${type}" ${extra}></div>`;}
function accountModal(){showModal('Войти в пространство',`<p class="form-note">Демонстрационные учётные записи предназначены для локальной защиты работы. Зритель видит только публичные данные.</p><div class="field"><label for="f-account">Учётная запись</label><select id="f-account" name="account"><option value="viewer">Зритель</option><option value="organizer">Организатор · демо</option><option value="captain-a">Капитан A · демо</option><option value="captain-b">Капитан B · демо</option><option value="custom">Свой токен</option></select></div><div id="custom-account" hidden><div class="field"><label for="f-role">Роль</label><select name="role" id="f-role"><option value="organizer">Организатор</option><option value="captain">Капитан</option></select></div>${field('Токен доступа','token','','password','autocomplete="off"')}<p class="form-note">Токен хранится только в текущей сессии браузера.</p></div>`,async form=>{const selected=form.get('account');if(selected==='custom'){if(!form.get('token'))throw new Error('Введите токен доступа.');setAccount({name:'Моя учётная запись',role:form.get('role'),label:'Свой токен доступа',token:form.get('token'),initial:'М'});}else setAccount(accounts[selected]);state.tournament=null;await loadList();await refresh();render();notify('Учётная запись изменена.');},{submit:'Продолжить',eyebrow:'ВАШЕ ПРОСТРАНСТВО'});
 $('#f-account').value=Object.keys(accounts).find(key=>accounts[key].token===account.token)||'custom';$('#custom-account').hidden=$('#f-account').value!=='custom';$('#f-account').onchange=()=>$('#custom-account').hidden=$('#f-account').value!=='custom';}
function requireOrganizer(){if(account.role!=='organizer'){accountModal();notify('Для этого действия выберите организатора.');return false;}return true;}
function localInput(date){const d=new Date(date);return new Date(d.getTime()-d.getTimezoneOffset()*60000).toISOString().slice(0,16);}
function createModal(){if(!requireOrganizer())return;const start=new Date();const end=new Date(start.getTime()+86400000);showModal('Новый турнир',`${field('Название турнира','name','','text','required maxlength="120" placeholder="Например, Кубок математической арены"')}${field('Идентификатор','id','tournament-'+Date.now().toString(36),'text','required pattern="[a-zA-Z0-9_-]+"')}<div class="field-grid">${field('Начало','starts',localInput(start),'datetime-local','required')}${field('Окончание','ends',localInput(end),'datetime-local','required')}</div><div class="spacer"></div>${field('Члены жюри, через запятую','jury','jury-1, jury-2, senior','text','required')}${field('Старший жюри','senior','senior','text','required')}<div class="field"><label for="f-regulation">Регламент</label><textarea id="f-regulation" name="regulation" required>Верно: стоимость задачи × множитель бланка. Неверно: 0. При равенстве баллов — общее место (1, 1, 3). Равенство суммы очков — совместные победители.</textarea></div><p class="form-note">В каждой команде ровно четыре участника. Игры и их задачи можно настроить после создания турнира.</p>`,async form=>{const id=form.get('id').trim();await api('/ui-api/tournament/tournaments',{method:'POST',body:{id,name:form.get('name').trim(),starts:new Date(form.get('starts')).toISOString(),ends:new Date(form.get('ends')).toISOString(),teamSize:4,jury:form.get('jury').split(',').map(x=>x.trim()).filter(Boolean),senior:form.get('senior').trim(),tiePolicy:'SharedCompetition',regulation:form.get('regulation').trim()}});await selectTournament(id);await loadList();setView('games');notify('Турнир создан. Теперь добавьте игры.');},{submit:'Создать турнир'});}
function taskEditor(task,index){return `<div class="task-editor"><div class="task-editor-heading"><strong>Задача ${index+1}</strong><button type="button" data-remove-task>Удалить</button></div>${field('Идентификатор задания','task-id',task.id,'text','required')}${field('Условие','statement',task.statement,'text','required')}${field('Эталон ответа','reference',task.reference,'text','required')}<div class="field-grid">${field('Баллы за верное решение','correct',task.correct??10,'number','required step="0.01"')}${field('Баллы за неверное решение','incorrect',task.incorrect??0,'number','required step="0.01"')}</div></div>`;}
function gameModal(id){
 if(!editable()){notify('Настройка доступна организатору до начала турнира.',true);return;}
 const existing=state.tournament.games.find(g=>gameConfig(g).id===id);const cfg=existing?.config||{id:'game-'+uid().slice(0,8),type:'Математическая карусель',tasks:[{id:'task-'+uid().slice(0,8),statement:'Вычислите 2 + 2',reference:'4',correct:10,incorrect:0}],modifiers:{normal:1,enhanced:1.5,risk:2},solutionSeconds:300,appealSeconds:420};
 showModal(existing?'Настроить игру':'Новая игра',`${field('Название / тип игры','type',cfg.type,'text','required')}${field('Идентификатор игры','id',cfg.id,'text',`required ${existing?'readonly':''}`)}<div class="field-grid">${field('Приём решений от старта, секунд','solutionSeconds',cfg.solutionSeconds,'number','required min="1"')}${field('Приём апелляций от старта, секунд','appealSeconds',cfg.appealSeconds,'number','required min="1"')}</div><p class="form-note">Оба срока отсчитываются от начала игры. Приём апелляций заканчивается не раньше приёма решений.</p><div class="subsection-title">Множители бланков</div><div id="modifier-editors" class="field-grid">${Object.entries(cfg.modifiers).map(([key,value])=>field(key,'modifier-'+key,value,'number','required min="0.01" step="0.01"')).join('')}</div><div class="subsection-title">Задачи и эталоны</div><div id="task-editors">${cfg.tasks.map(taskEditor).join('')}</div><button type="button" class="button secondary wide" id="add-task">${icon('plus')}Добавить задачу</button>`,async form=>{const tasks=$$('.task-editor',$('#modal-body')).map(node=>{const val=name=>$(`[name="${name}"]`,node).value;return{id:val('task-id').trim(),statement:val('statement').trim(),reference:val('reference').trim(),correct:Number(val('correct')),incorrect:Number(val('incorrect'))};});if(!tasks.length)throw new Error('Добавьте хотя бы одну задачу.');const modifiers=Object.fromEntries($$('#modifier-editors input').map(input=>[input.name.slice(9),Number(input.value)]));await command('/games/'+encodeURIComponent(form.get('id')), {id:form.get('id'),type:form.get('type'),tasks,modifiers,solutionSeconds:Number(form.get('solutionSeconds')),appealSeconds:Number(form.get('appealSeconds'))},'PUT');await refresh();notify('Настройки игры сохранены.');});
 $('#add-task').onclick=()=>{const container=$('#task-editors');container.insertAdjacentHTML('beforeend',taskEditor({id:'task-'+uid().slice(0,8),statement:'',reference:'',correct:10,incorrect:0},container.children.length));};
 $('#task-editors').onclick=event=>{const button=event.target.closest('[data-remove-task]');if(button)button.closest('.task-editor').remove();};
}
function registrationModal(){if(account.role!=='captain'){accountModal();return;}const captain=account.token==='captain-a-demo'?'captain-a':account.token==='captain-b-demo'?'captain-b':'';showModal('Команда на арене',`${field('Название команды','name','','text','required placeholder="Например, Интеграл"')}${field('Идентификатор команды','id','team-'+uid().slice(0,8),'text','required')}${field('Идентификатор капитана','captain',captain,'text','required')}<p class="form-note">Капитан — первый участник команды. Его идентификатор должен соответствовать учётной записи. Добавьте ещё троих участников.</p>${[1,2,3].map(i=>field('Участник '+(i+1),'member-'+i,'','text','required')).join('')}`,async form=>{const captainId=form.get('captain').trim();await command('/teams',{id:form.get('id').trim(),name:form.get('name').trim(),captain:captainId,members:[captainId,...[1,2,3].map(i=>form.get('member-'+i).trim())]});await refresh();notify('Команда зарегистрирована. Увидимся на арене!');},{submit:'Зарегистрировать команду',eyebrow:'РОВНО ЧЕТЫРЕ УЧАСТНИКА'});}
function confirmModal(title,message,action,label='Подтвердить'){showModal(title,`<p class="form-note">${escapeHtml(message)}</p>`,action,{submit:label});}
function demoModal(){showModal('Посмотрите турнир в действии',`<div class="form-note">Будет создан отдельный демонстрационный турнир: две команды, две игры, верные и неверные решения, удовлетворённая и отклонённая апелляции.</div><p class="form-note">Вы войдёте как демонстрационный организатор. Вердикты отправит тестовый имитатор. Все баллы и итоги рассчитают рабочие сервисы. Сценарий займёт около 45 секунд.</p>`,async()=>{setAccount(accounts.organizer);$('#modal').close();await runDemo();},{submit:'Запустить демонстрацию',eyebrow:'СЦЕНАРИЙ ДЛЯ ЗАЩИТЫ'});}
async function retryFixture(id,game){for(let i=0;i<30;i++){try{return await api('/ui-api/fixtures/'+encodeURIComponent(id)+'/'+encodeURIComponent(game),{method:'POST'});}catch(error){if(error.status!==500&&error.status!==409&&error.status!==502)throw error;if(i===29)throw error;await pause(700);}}}
async function until(predicate,timeout=65000){const end=Date.now()+timeout;while(Date.now()<end){const value=await predicate();if(value)return value;await pause(650);}throw new Error('Сервисы ещё обрабатывают события. Турнир сохранён; его можно продолжить вручную.');}
async function runDemo(){
 if(state.demonstrating)return;state.demonstrating=true;$('#demo-banner').hidden=false;$('#demo-button').disabled=true;$('#create-button').disabled=true;
 const progress=text=>$('#demo-progress').textContent=text;const id='arena-'+Date.now().toString(36);
 try{
  progress('Создаём турнир и настраиваем игры');const now=new Date();
  await api('/ui-api/tournament/tournaments',{method:'POST',body:{id,name:'Кубок математической арены',starts:now.toISOString(),ends:new Date(now.getTime()+86400000).toISOString(),teamSize:4,jury:['jury-1','jury-2','senior'],senior:'senior',tiePolicy:'SharedCompetition',regulation:'Верно: 10 баллов × множитель бланка. Неверно: 0. Бланки: обычный ×1, усиленный ×1,5, рискованный ×2. При равенстве — общее место (1, 1, 3). Равенство итоговой суммы — совместные победители.'}});
  for(let i=1;i<=2;i++)await command('/games/game'+i,{id:'game'+i,type:i===1?'Математическая карусель':'Гонка решений',tasks:[{id:`g${i}-task1`,statement:'Вычислите 2 + 2',reference:'4',correct:10,incorrect:0},{id:`g${i}-task2`,statement:'Вычислите 3 × 3',reference:'9',correct:10,incorrect:0}],modifiers:{normal:1,enhanced:1.5,risk:2},solutionSeconds:12,appealSeconds:20},'PUT',account.token,id);
  await command('/publish',undefined,'POST',account.token,id);progress('Регистрация двух команд по четыре участника');
  for(const [suffix,name] of [['a','Интеграл'],['b','Квадрат мысли']])await command('/teams',{id:suffix,name,captain:'captain-'+suffix,members:['captain-'+suffix,suffix+'1',suffix+'2',suffix+'3']},'POST','captain-'+suffix+'-demo',id);
  await command('/start',undefined,'POST',account.token,id);await selectTournament(id);await loadList();setView('overview');
  for(let i=1;i<=2;i++){
   progress(`Игра ${i} из 2 · запуск и отправка тестовых вердиктов`);await command('/games/game'+i+'/start',undefined,'POST',account.token,id);await refresh();
   await retryFixture(id,'game'+i);progress(`Игра ${i} из 2 · ждём закрытия приёмов и подведения итогов`);
   await until(async()=>{const t=await api(tournamentPath('',id));return t.games.some(g=>g.config.id==='game'+i&&g.status==='Completed');});await refresh();
  }
  await until(async()=>{const t=await api(tournamentPath('',id));return t.status==='Completed';});
  await until(async()=>{const table=await api('/tournaments/'+encodeURIComponent(id)+'/table');return table.complete;});
  await refresh();await loadList();progress('Готово · все игры завершены');notify('Турнир завершён. Интеграл — 200 очков, Квадрат мысли — 196.');setView('results');
 }catch(error){progress('Демонстрация остановлена');notify(error.message,true);}
 finally{state.demonstrating=false;$('#demo-button').disabled=false;$('#create-button').disabled=false;setTimeout(()=>$('#demo-banner').hidden=true,6000);}
}
function exportCsv(){const rows=state.table?.rows;if(!rows?.length){notify('Пока нет результатов для выгрузки.');return;}const quote=value=>'"'+String(value).replace(/"/g,'""')+'"';const safe=value=>/^[=+\-@]/.test(String(value))?"'"+value:value;const csv=[['Место','Команда',...state.table.games.map((_,i)=>`Очки за игру ${i+1}`),'Сумма'],...rows.map(row=>[row.place,safe(row.name),...row.games.map(g=>g.final?.points??''),row.total])].map(row=>row.map(quote).join(';')).join('\r\n');const url=URL.createObjectURL(new Blob(['\ufeff'+csv],{type:'text/csv;charset=utf-8'}));const link=document.createElement('a');link.href=url;link.download='arena-'+state.id+'.csv';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);notify('Таблица сохранена в CSV.');}
document.addEventListener('click',async event=>{
 const b=event.target.closest('button');if(!b||b.disabled)return;
 if(b.dataset.view){setView(b.dataset.view);return;}if(b.dataset.game){state.game=b.dataset.game;render();return;}
 if(b.dataset.edit){gameModal(b.dataset.edit);return;}
 if(b.dataset.delete){const id=b.dataset.delete;confirmModal('Удалить игру?','Настройки и задачи этой игры будут удалены из программы турнира.',async()=>{await command('/games/'+encodeURIComponent(id),undefined,'DELETE');await refresh();notify('Игра удалена.');},'Удалить игру');return;}
 if(b.dataset.start){const id=b.dataset.start;confirmModal('Начать игру?','С этого момента начнётся отсчёт времени приёма решений и апелляций.',async()=>{await command('/games/'+encodeURIComponent(id)+'/start');await refresh();notify('Игра началась. Удачи командам!');},'Начать игру');return;}
 const action=b.dataset.action;
 if(action==='reject-forms'){try{await api('/ui-api/lab/reject-next-forms',{method:'POST'});notify('Учебный режим: следующая подготовка бланков завершится отказом. Запустите настроенную игру и наблюдайте компенсацию.');}catch(error){notify(error.message,true);}return;}
 if(action==='demo')demoModal();if(action==='register')registrationModal();if(action==='add-game')gameModal();if(action==='export')exportCsv();
 if(action==='publish')confirmModal('Открыть регистрацию?','Турнир и регламент станут доступны командам.',async()=>{await command('/publish');await refresh();await loadList();notify('Регистрация открыта.');},'Опубликовать турнир');
 if(action==='start')confirmModal('Начать турнир?','Регистрация закроется. Изменять игры и состав участников будет нельзя.',async()=>{await command('/start');await refresh();await loadList();notify('Турнир начат. Теперь можно запустить первую игру.');},'Начать турнир');
});
$('#modal-form').onsubmit=async event=>{event.preventDefault();if(modalBusy)return;modalBusy=true;$('#modal-submit').disabled=true;$('#form-error').textContent='';try{await modalHandler(new FormData(event.target));$('#modal').close();}catch(error){$('#form-error').textContent=error.message;}finally{modalBusy=false;$('#modal-submit').disabled=false;}};
$('#modal').addEventListener('cancel',event=>{if(modalBusy)event.preventDefault();});$('#close-modal').onclick=closeModal;$('#cancel-modal').onclick=closeModal;
$('#create-button').onclick=createModal;$('#demo-button').onclick=demoModal;$('#account-button').onclick=accountModal;
$('#refresh-button').onclick=async()=>{await loadList().catch(error=>notify(error.message,true));await refresh();};
$('#tournament-select').onchange=event=>{if(state.demonstrating){event.target.value=state.id;notify('Дождитесь завершения демонстрации.');return;}selectTournament(event.target.value);};
document.addEventListener('input',event=>{if(event.target.id==='team-search'){state.search=event.target.value;render();}});
window.addEventListener('hashchange',()=>{const view=location.hash.slice(1);if(viewNames[view]){state.view=view;render();}});
setInterval(()=>{if(!document.hidden)refresh({quiet:true});},2200);
setInterval(()=>{$$('[data-deadline]').forEach(node=>node.textContent=remaining(node.dataset.deadline));},1000);
updateAccount();render();loadList().then(()=>refresh()).then(connectStream).catch(error=>{render(error.message);notify(error.message,true);});

api('/lab/status').then(info=>{state.labEnabled=info.enabled;render();}).catch(()=>{});
