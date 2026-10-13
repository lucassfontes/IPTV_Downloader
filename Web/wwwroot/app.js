/* IPTV Downloader 2.0.26 — interface web; o download permanece no C# do projeto original. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const PAGE_SIZE = 30;
  const state = {
    connected: false, kind: 'movies', path: { level: 'catalog', seriesId: '', seriesName: '', season: 0 },
    lists: { movies: null, series: null }, current: [], selected: new Set(),
    page: 1, letter: '', searching: false, loading: false, jobsVisible: false,
    search: '', category: '', seq: 0, poll: null
  };
  const normalize = value => String(value ?? '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase('pt-BR');
  const el = (tag, className, text) => { const x = document.createElement(tag); if (className) x.className = className; if (text !== undefined) x.textContent = text; return x; };
  const readableNumber = n => Number(n || 0).toLocaleString('pt-BR');
  const bytes = num => { const n = Number(num || 0); if (!n) return '0 B'; const i = Math.min(Math.floor(Math.log(n) / Math.log(1024)), 4); return `${(n / 1024 ** i).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} ${['B', 'KB', 'MB', 'GB', 'TB'][i]}`; };
  const speed = num => num ? `${bytes(num)}/s` : '—';
  const safeImage = src => { try { const u = new URL(src); return (u.protocol === 'https:' || u.protocol === 'http:') ? u.href : null; } catch { return null; } };
  const toast = (msg, error = false) => { const x = $('toast'); x.textContent = msg; x.classList.toggle('error', error); x.classList.add('visible'); clearTimeout(toast.timer); toast.timer = setTimeout(() => x.classList.remove('visible'), 5200); };
  const request = async (path, data, method = 'GET') => {
    const opt = { method, headers: { 'Accept': 'application/json' } };
    if (data !== undefined) { opt.method = 'POST'; opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(data); }
    let response;
    try { response = await fetch(path, opt); } catch { throw new Error('Servidor C# não respondeu. Verifique se está iniciado.'); }
    let result = null;
    try { result = await response.json(); } catch { /* parse optional */ }
    if (!response.ok) throw new Error(result?.error || `Erro HTTP ${response.status}`);
    return result;
  };
  function setBusy(value, message) {
    state.loading = value;
    if (value && message) empty(message, 'Carregando');
    for (const id of ['connectButton', 'connectSaved', 'refreshCatalog']) $(id).disabled = value;
  }
  function empty(text, title = 'Nenhum item encontrado') {
    const box = el('div', 'empty'); box.append(el('div', 'empty-symbol', '▣'), el('strong', '', title), el('p', '', text));
    $('cards').replaceChildren(box);
    $('counter').textContent = '0 itens';
  }
  async function refreshState() {
    const r = await request('/api/state');
    state.connected = Boolean(r.connected);
    $('accountStatus').classList.toggle('connected', state.connected);
    $('accountStatus').lastChild.textContent = ` ${state.connected ? `Conectado · ${r.account}` : 'Desconectado'}`;
    $('downloadParent').value = r.downloadParent || '';
    $('hideAdult').checked = r.hideAdult !== false;
    $('outputPath').textContent = `Destino: ${r.downloadRoot || 'pasta padrão'}. As subpastas de filmes, séries e temporadas são criadas automaticamente no servidor.`;
    const select = $('savedAccounts'); const previous = select.value;
    select.replaceChildren(el('option', '', r.savedAccounts?.length ? 'Selecione uma conta' : 'Nenhuma conta salva'));
    select.firstChild.value = '';
    for (const key of r.savedAccounts || []) { const item = el('option', '', key); item.value = key; select.append(item); }
    if (r.savedAccounts?.includes(previous)) select.value = previous;
    if (state.connected) $('toggleConfig').textContent = $('configPanel').classList.contains('hidden') ? 'Mostrar configurações' : 'Ocultar configurações';
  }
  function categories() {
    const selector = $('category');
    const selected = state.category;
    const groups = [...new Set(state.current.map(x => x.group).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'pt-BR', { numeric: true }));
    selector.replaceChildren(el('option', '', 'Todas as categorias'));
    selector.firstChild.value = '';
    for (const group of groups) { const option = el('option', '', group); option.value = group; selector.append(option); }
    if (groups.includes(selected)) selector.value = selected;
    else { state.category = ''; selector.value = ''; }
  }
  function currentFiltered() {
    const q = normalize(state.search), letter = state.letter;
    return state.current.filter(x => (!state.category || x.group === state.category)
      && (!q || normalize(`${x.name} ${x.group} ${x.year} ${x.genre}`).includes(q))
      && (!letter || (normalize(x.name).charAt(0).toUpperCase() === letter)));
  }
  function visiblePage() { const filtered = currentFiltered(); const pages = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE)); state.page = Math.max(1, Math.min(state.page, pages)); return filtered.slice((state.page - 1) * PAGE_SIZE, state.page * PAGE_SIZE); }
  function renderAlphabet() {
    const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ#';
    const alpha = $('alphabet'); alpha.replaceChildren();
    const all = el('button', `alpha ${!state.letter ? 'active' : ''}`, 'Tudo'); all.style.fontSize = '9px'; all.setAttribute('aria-label', 'Mostrar todas as letras'); all.onclick = () => setLetter(''); alpha.append(all);
    const available = new Set(state.current.map(x => normalize(x.name).charAt(0).toUpperCase()).map(s => /[A-Z]/.test(s) ? s : '#'));
    for (const letter of letters) { const b = el('button', `alpha ${state.letter === letter ? 'active' : ''}`, letter); b.disabled = !available.has(letter); b.onclick = () => setLetter(letter); alpha.append(b); }
  }
  function setLetter(letter) {
    state.letter = letter; state.page = 1; render();
  }
  function makeCard(item, idx) {
    const card = el('article', `card ${state.selected.has(item.id) ? 'selected' : ''}`);
    const cover = el('div', 'poster'); const src = safeImage(item.logo);
    if (src) { const img = el('img'); img.src = src; img.loading = 'lazy'; img.alt = ''; img.referrerPolicy = 'no-referrer'; img.onerror = () => { cover.replaceChildren(el('span', '', '▣')); }; cover.append(img); }
    else cover.textContent = '▣';
    const main = el('div', 'card-content');
    const name = el('h3', 'card-name', `${idx + 1}. ${item.name || 'Sem título'}`);
    const tags = [item.group, item.year, item.rating ? `★ ${item.rating}` : '', item.genre].filter(Boolean);
    main.append(name, el('div', 'card-meta', tags.join('  ·  ')));
    if (item.plot) main.append(el('div', 'card-plot', item.plot));
    if (item.type === 'season') main.append(el('div', 'card-meta', `${item.episodeCount || 0} episódios`));
    const actions = el('div', 'card-actions');
    const isFolder = item.type === 'series' || item.type === 'season';
    if (isFolder) {
      const open = el('button', 'btn primary', item.type === 'series' ? 'Ver temporadas' : 'Ver episódios');
      open.type = 'button'; open.onclick = () => openFolder(item); actions.append(open);
    }
    const queue = el('button', 'btn', item.type === 'series' ? 'Baixar série' : item.type === 'season' ? 'Baixar temporada' : 'Baixar');
    queue.type = 'button'; queue.onclick = () => enqueue([item.id]); actions.append(queue);
    const sel = el('button', 'btn', state.selected.has(item.id) ? '✓ Selecionado' : 'Selecionar');
    sel.type = 'button'; sel.setAttribute('aria-pressed', state.selected.has(item.id));
    sel.onclick = () => { state.selected.has(item.id) ? state.selected.delete(item.id) : state.selected.add(item.id); updateSelection(); card.classList.toggle('selected', state.selected.has(item.id)); sel.textContent = state.selected.has(item.id) ? '✓ Selecionado' : 'Selecionar'; sel.setAttribute('aria-pressed', state.selected.has(item.id)); };
    actions.append(sel); card.append(cover, main, actions); return card;
  }
  function render() {
    $('moviesTab').classList.toggle('active', state.kind === 'movies'); $('seriesTab').classList.toggle('active', state.kind === 'series');
    $('back').disabled = state.path.level === 'catalog';
    const filtered = currentFiltered(), pages = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE));
    state.page = Math.min(Math.max(1, state.page), pages);
    const start = (state.page - 1) * PAGE_SIZE;
    const current = visiblePage();
    $('counter').textContent = `${readableNumber(filtered.length)} ${state.path.level === 'episodes' ? 'episódios' : state.path.level === 'seasons' ? 'temporadas' : 'itens'}`;
    $('pageCount').textContent = `Página ${state.page} / ${pages}`;
    $('prevPage').disabled = state.page <= 1; $('nextPage').disabled = state.page >= pages;
    const list = $('cards'); list.replaceChildren();
    if (!current.length) empty(state.connected ? 'Tente outra busca, categoria ou letra.' : 'Conecte sua conta IPTV em Conexão.', state.connected ? 'Nenhum item encontrado' : 'Conecte sua conta IPTV');
    else { const fragment = document.createDocumentFragment(); current.forEach((item, i) => fragment.append(makeCard(item, start + i))); list.append(fragment); }
    renderAlphabet(); updateSelection();
  }
  function resetFilters() { state.page = 1; state.letter = ''; state.category = ''; state.selected.clear(); $('search').value = ''; state.search = ''; }
  async function loadCatalog(kind, force = false) {
    if (!state.connected) { empty('Informe servidor, usuário e senha para carregar o conteúdo.', 'Conecte sua conta IPTV'); return; }
    state.kind = kind; state.path = { level: 'catalog', seriesId: '', seriesName: '', season: 0 }; resetFilters();
    if (force) state.lists[kind] = null;
    if (state.lists[kind]) { state.current = state.lists[kind]; categories(); render(); return; }
    const seq = ++state.seq; setBusy(true, `Carregando ${kind === 'movies' ? 'filmes' : 'séries'}…`);
    try { const items = await request(`/api/catalog/${kind}`); if (seq !== state.seq) return; state.lists[kind] = items; state.current = items; categories(); render(); toast(`${readableNumber(items.length)} ${kind === 'movies' ? 'filmes' : 'séries'} carregados.`); }
    catch (e) { if (seq === state.seq) { empty(e.message, 'Falha ao carregar'); toast(e.message, true); } }
    finally { setBusy(false); }
  }
  async function openFolder(item) {
    if (state.loading) return;
    const prev = { ...state.path }; const prevData = state.current;
    resetFilters(); setBusy(true, item.type === 'series' ? 'Carregando temporadas…' : 'Carregando episódios…');
    try {
      let items;
      if (item.type === 'series') {
        items = await request(`/api/series/${encodeURIComponent(item.seriesId)}/seasons`);
        state.path = { level: 'seasons', seriesId: item.seriesId, seriesName: item.name, season: 0 };
      } else {
        items = await request(`/api/series/${encodeURIComponent(item.seriesId)}/season/${item.season}`);
        items.sort((a, b) => (Number(a.episode) || 0) - (Number(b.episode) || 0) || String(a.name).localeCompare(String(b.name), 'pt-BR', { numeric: true }));
        state.path = { level: 'episodes', seriesId: item.seriesId, seriesName: prev.seriesName, season: item.season };
      }
      state.current = items; categories(); render();
    } catch (e) { state.path = prev; state.current = prevData; categories(); render(); toast(e.message, true); }
    finally { setBusy(false); }
  }
  async function back() {
    if (state.loading) return;
    if (state.path.level === 'episodes') {
      const id = state.path.seriesId; const name = state.path.seriesName;
      resetFilters(); setBusy(true, 'Carregando temporadas…');
      try { state.current = await request(`/api/series/${encodeURIComponent(id)}/seasons`); state.path = { level: 'seasons', seriesId: id, seriesName: name, season: 0 }; categories(); render(); }
      catch (e) { toast(e.message, true); } finally { setBusy(false); }
    } else if (state.path.level === 'seasons') { await loadCatalog('series'); }
  }
  async function enqueue(keys) {
    if (!keys.length) { toast('Selecione pelo menos um item.', true); return; }
    try { const result = await request('/api/queue', { keys }); toast(result.added > 0 ? `${result.added} arquivo(s) adicionado(s) à fila.` : 'Esses arquivos já estão na fila ou não foram encontrados.'); state.selected.clear(); render(); await pollDownloads(); }
    catch (e) { toast(e.message, true); }
  }
  function updateSelection() { $('selectionCount').textContent = `(${state.selected.size})`; $('downloadSelected').disabled = !state.selected.size; }
  let dataPaused = false;
  function renderJobs(data) {
    dataPaused = Boolean(data.paused);
    const jobs = data.jobs || [];
    const active = jobs.find(j => j.status === 'Baixando' || j.status === 'Pausado');
    const pending = Number(data.pending || 0);
    $('jobSummary').textContent = `${pending} na fila · ${jobs.filter(j => j.status === 'Concluído').length} concluído(s)`;
    $('pauseDownload').textContent = data.paused ? 'Continuar' : 'Pausar';
    $('pauseDownload').disabled = !active;
    $('cancelDownload').disabled = !active;
    const fraction = Math.max(0, Math.min(1, Number(active?.progress || 0)));
    const last = jobs[0];
    $('progressBar').style.width = `${active ? fraction * 100 : last?.status === 'Concluído' ? 100 : 0}%`;
    $('progressBar').classList.toggle('finished', !active && last?.status === 'Concluído');
    $('currentStatus').textContent = active ? `${active.name} · ${Math.round(fraction * 100)}% · ${bytes(active.bytes)}${active.totalBytes ? ` / ${bytes(active.totalBytes)}` : ''}` : last ? `${last.name} · ${last.status}` : 'Nenhum download iniciado';
    $('currentSpeed').textContent = active ? speed(active.speed) : '—';
    if (!state.jobsVisible) return;
    const box = $('jobs'); box.replaceChildren();
    if (!jobs.length) box.append(el('p', 'hint', 'Nenhum download nesta sessão.'));
    for (const item of jobs) {
      const row = el('div', `job ${item.status === 'Concluído' ? 'completed' : item.status === 'Erro' ? 'error' : ''}`);
      const name = el('span', '', item.name); name.title = item.name;
      row.append(name, el('span', '', `${item.status} · ${Math.round((item.progress || 0) * 100)}%`)); box.append(row);
    }
  }
  async function pollDownloads() { try { renderJobs(await request('/api/downloads')); } catch (e) { if (!pollDownloads.offline) toast(e.message, true); pollDownloads.offline = true; return; } pollDownloads.offline = false; }
  function on(id, event, fn) { $(id).addEventListener(event, fn); }
  function wireEvents() {
    document.querySelectorAll('[data-config]').forEach(button => button.addEventListener('click', () => {
      document.querySelectorAll('[data-config]').forEach(b => { b.classList.toggle('active', b === button); b.setAttribute('aria-selected', b === button ? 'true' : 'false'); });
      document.querySelectorAll('.tab-body').forEach(b => b.classList.toggle('hidden', b.id !== `config-${button.dataset.config}`));
    }));
    on('toggleConfig', 'click', () => { const h = $('configPanel').classList.toggle('hidden'); $('toggleConfig').textContent = h ? 'Mostrar configurações' : 'Ocultar configurações'; });
    on('connectForm', 'submit', async ev => {
      ev.preventDefault(); setBusy(true, 'Conectando à lista IPTV…');
      try {
        await request('/api/connect', { server: $('server').value.trim(), username: $('username').value.trim(), password: $('password').value, remember: $('remember').checked });
        $('password').value = ''; state.lists = { movies: null, series: null }; await refreshState();
        $('configPanel').classList.add('hidden'); $('toggleConfig').textContent = 'Mostrar configurações';
        await loadCatalog('movies', true);
      } catch (e) { toast(e.message, true); empty(e.message, 'Falha na conexão'); }
      finally { setBusy(false); }
    });
    on('connectSaved', 'click', async () => {
      const key = $('savedAccounts').value; if (!key) { toast('Selecione uma conta salva.', true); return; }
      setBusy(true, 'Conectando à conta salva…');
      try { await request('/api/connect-saved', { key }); state.lists = { movies: null, series: null }; await refreshState(); $('configPanel').classList.add('hidden'); $('toggleConfig').textContent = 'Mostrar configurações'; await loadCatalog('movies', true); }
      catch (e) { toast(e.message, true); } finally { setBusy(false); }
    });
    on('refreshCatalog', 'click', () => loadCatalog(state.kind, true));
    on('settingsForm', 'submit', async ev => {
      ev.preventDefault(); try { await request('/api/settings', { downloadParent: $('downloadParent').value, hideAdult: $('hideAdult').checked }); state.lists = { movies: null, series: null }; await refreshState(); toast('Configurações salvas.'); if (state.connected) await loadCatalog(state.kind, true); }
      catch (e) { toast(e.message, true); }
    });
    on('moviesTab', 'click', () => loadCatalog('movies'));
    on('seriesTab', 'click', () => loadCatalog('series'));
    on('search', 'input', ev => { state.search = ev.target.value; state.page = 1; render(); });
    on('category', 'change', ev => { state.category = ev.target.value; state.page = 1; render(); });
    on('back', 'click', back);
    on('prevPage', 'click', () => { state.page--; render(); $('cards').scrollTop = 0; });
    on('nextPage', 'click', () => { state.page++; render(); $('cards').scrollTop = 0; });
    on('selectVisible', 'click', () => { for (const x of visiblePage()) state.selected.add(x.id); render(); });
    on('clearSelection', 'click', () => { state.selected.clear(); render(); });
    on('downloadSelected', 'click', () => enqueue([...state.selected]));
    on('pauseDownload', 'click', async () => { try { await request(dataPaused ? '/api/downloads/resume' : '/api/downloads/pause', {}); await pollDownloads(); } catch (e) { toast(e.message, true); } });
    on('cancelDownload', 'click', async () => { if (!confirm('Cancelar o download em andamento? Os demais arquivos da fila continuam.')) return; try { await request('/api/downloads/cancel', {}); await pollDownloads(); } catch (e) { toast(e.message, true); } });
    on('showJobs', 'click', () => { state.jobsVisible = !state.jobsVisible; $('jobs').classList.toggle('hidden', !state.jobsVisible); $('showJobs').textContent = state.jobsVisible ? 'Ocultar fila' : 'Mostrar fila'; pollDownloads(); });
  }
  async function init() {
    wireEvents(); render();
    try { await refreshState(); if (state.connected) await loadCatalog('movies'); }
    catch (e) { toast(e.message, true); empty(e.message, 'Sem conexão com o servidor'); }
    await pollDownloads(); state.poll = window.setInterval(() => { if (!document.hidden || state.jobsVisible) pollDownloads(); }, 1300);
  }
  init();
})();
