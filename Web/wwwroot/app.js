/* IPTV Downloader 2.0.28 — interface web; o download permanece no C# do projeto original. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const PAGE_SIZE = 30;
  const state = {
    connected: false, kind: 'movies', path: { level: 'catalog', seriesId: '', seriesName: '', season: 0 },
    lists: { movies: null, series: null }, current: [], selected: new Set(),
    page: 1, letter: '', searching: false, loading: false, jobsVisible: true,
    search: '', category: '', seq: 0, poll: null
  };
  const normalize = value => String(value ?? '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase('pt-BR');
  const el = (tag, className, text) => { const x = document.createElement(tag); if (className) x.className = className; if (text !== undefined) x.textContent = text; return x; };
  const readableNumber = n => Number(n || 0).toLocaleString('pt-BR');
  const bytes = num => { const n = Number(num || 0); if (!n) return '0 B'; const i = Math.min(Math.floor(Math.log(n) / Math.log(1024)), 4); return `${(n / 1024 ** i).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} ${['B', 'KB', 'MB', 'GB', 'TB'][i]}`; };
  const speed = num => num ? `${bytes(num)}/s` : '—';
  const safeImage = src => { try { const u = new URL(src); return (u.protocol === 'https:' || u.protocol === 'http:') ? u.href : null; } catch { return null; } };
  const toast = (msg, error = false) => { const x = $('toast'); x.textContent = msg; x.classList.toggle('error', error); x.classList.add('visible'); clearTimeout(toast.timer); toast.timer = setTimeout(() => x.classList.remove('visible'), 5200); };
  let dialogResolver = null;
  let previousFocus = null;
  function closeAppDialog(result) {
    const overlay = $('appDialog');
    overlay.classList.add('hidden'); overlay.setAttribute('aria-hidden', 'true');
    const resolve = dialogResolver; dialogResolver = null;
    if (previousFocus?.isConnected) previousFocus.focus();
    previousFocus = null;
    if (resolve) resolve(result);
  }
  function appDialog(message, { title = 'Confirmação', confirmLabel = 'Confirmar', cancelLabel = 'Cancelar', danger = false, path = '' } = {}) {
    if (dialogResolver) closeAppDialog(false);
    previousFocus = document.activeElement;
    $('dialogTitle').textContent = title;
    $('dialogMessage').textContent = message;
    $('dialogPathArea').classList.toggle('hidden', !path);
    $('dialogPath').value = path;
    $('dialogCancel').textContent = cancelLabel;
    $('dialogCancel').classList.toggle('hidden', Boolean(path));
    $('dialogOk').textContent = path ? 'Fechar' : confirmLabel;
    $('dialogOk').classList.toggle('danger', danger);
    $('dialogSymbol').textContent = danger ? '!' : path ? '📁' : '?';
    $('appDialog').classList.remove('hidden'); $('appDialog').setAttribute('aria-hidden', 'false');
    $('dialogOk').focus();
    return new Promise(resolve => { dialogResolver = resolve; });
  }
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
    if (value && message) showLoading(message);
    for (const id of ['connectButton', 'connectSaved', 'refreshCatalog']) $(id).disabled = value;
  }
  function showLoading(message) {
    const wrapper = el('div', 'loading-state');
    wrapper.setAttribute('role', 'status');
    wrapper.setAttribute('aria-live', 'polite');
    const spinner = el('div', 'loading-spinner'); spinner.setAttribute('aria-hidden', 'true');
    const text = el('strong', 'loading-heading', message || 'Carregando conteúdo…');
    const subtitle = el('span', 'loading-subtitle', 'Aguarde enquanto buscamos os itens da sua biblioteca.');
    const tracks = el('div', 'loading-track');
    for (let i = 0; i < 3; i++) tracks.append(el('span', 'loading-shimmer'));
    wrapper.append(spinner, text, subtitle, tracks);
    $('cards').replaceChildren(wrapper);
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
    $('downloadConnections').value = String(r.downloadConnections || 1);
    $('outputPath').textContent = `Destino atual: ${r.downloadRoot || 'pasta padrão'}. As subpastas de filmes, séries e temporadas são criadas automaticamente.`;
    const select = $('savedAccounts'); const previous = select.value;
    select.replaceChildren(el('option', '', r.savedAccounts?.length ? 'Selecione uma conta' : 'Nenhuma conta salva'));
    select.firstChild.value = '';
    for (const key of r.savedAccounts || []) { const item = el('option', '', key); item.value = key; select.append(item); }
    if (r.savedAccounts?.includes(previous)) select.value = previous;

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
    const name = el('h3', 'card-name', item.name || 'Sem título');
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
    const toggleSelection = () => {
      state.selected.has(item.id) ? state.selected.delete(item.id) : state.selected.add(item.id);
      updateSelection();
      const selected = state.selected.has(item.id);
      card.classList.toggle('selected', selected);
      sel.textContent = selected ? '✓ Selecionado' : 'Selecionar';
      sel.setAttribute('aria-pressed', selected);
      if (!isFolder) card.setAttribute('aria-selected', selected);
    };
    sel.onclick = toggleSelection;
    const activateCard = () => isFolder ? openFolder(item) : toggleSelection();
    card.tabIndex = 0;
    card.setAttribute('aria-label', isFolder ? `Abrir ${item.name}` : `Selecionar ${item.name}`);
    card.addEventListener('click', event => {
      if (event.target.closest('button, input, select, a')) return;
      activateCard();
    });
    card.addEventListener('keydown', event => {
      if (event.target !== card || !['Enter', ' '].includes(event.key)) return;
      event.preventDefault(); activateCard();
    });
    actions.append(sel); card.append(cover, main, actions); return card;
  }
  function render() {
    $('moviesTab').classList.toggle('active', state.kind === 'movies'); $('seriesTab').classList.toggle('active', state.kind === 'series');
    $('back').disabled = state.path.level === 'catalog';
    $('breadcrumb').textContent = state.path.level === 'catalog' ? (state.kind === 'series' ? 'Séries' : 'Filmes')
      : `${state.path.seriesName} / ${state.path.level === 'episodes' ? 'Temporada ' + state.path.season + ' / Episódios' : 'Temporadas'}`;
    const browseFolder = state.path.level !== 'catalog';
    $('alphabet').classList.toggle('hidden', browseFolder);
    $('category').disabled = browseFolder;
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
        items.sort((a, b) => (Number(a.episode) > 0 ? Number(a.episode) : Number.MAX_SAFE_INTEGER) - (Number(b.episode) > 0 ? Number(b.episode) : Number.MAX_SAFE_INTEGER) || String(a.name).localeCompare(String(b.name), 'pt-BR', { numeric: true }));
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
  async function openDownloadFolder(id, button) {
    button.disabled = true;
    try { const result = await request(`/api/downloads/${encodeURIComponent(id)}/open-folder`, {}); if (result.opened) toast('Pasta aberta no computador.'); else { toast(result.message || 'Não foi possível abrir a pasta neste dispositivo.', true); if (result.path) { try { await navigator.clipboard.writeText(result.path); toast('Caminho da pasta copiado para a área de transferência.'); } catch { await appDialog('Não foi possível copiar automaticamente. Copie o caminho abaixo.', { title: 'Pasta do download', path: result.path }); } } } }
    catch (e) { toast(e.message, true); button.disabled = false; return; }
    button.disabled = false;
  }
  async function deleteDownloadItem(id, button) {
    if (!await appDialog('Excluir o arquivo baixado e remover este item da lista?', { title: 'Excluir download', confirmLabel: 'Excluir', danger: true })) return;
    button.disabled = true;
    try { await request(`/api/downloads/${encodeURIComponent(id)}/delete`, {}); toast('Arquivo removido.'); await pollDownloads(); }
    catch (e) { toast(e.message, true); button.disabled = false; }
  }
  let dataPaused = false;
  function renderJobs(data) {
    dataPaused = Boolean(data.paused);
    const jobs = data.jobs || [];
    const active = jobs.find(j => j.id === data.currentId);
    const pending = Number(data.pending || 0);
    const remaining = jobs.filter(j => !['Concluído', 'Cancelado'].includes(j.status)).length;
    $('downloadBadge').textContent = remaining;
    $('downloadBadge').hidden = remaining === 0;
    $('viewDownloads').setAttribute('aria-label', `Downloads: ${remaining} pendentes`);
    $('jobSummary').textContent = `${pending} na fila · ${jobs.filter(j => j.status === 'Concluído').length} concluído(s)`;
    $('pauseDownload').textContent = data.paused ? 'Continuar' : 'Pausar';
    $('pauseDownload').disabled = !active;
    $('cancelDownload').disabled = !active;
    const fraction = Math.max(0, Math.min(1, Number(active?.progress || 0)));
    const last = jobs[0];
    $('progressBar').style.width = `${active ? fraction * 100 : last?.status === 'Concluído' ? 100 : 0}%`;
    $('progressBar').classList.toggle('finished', !active && last?.status === 'Concluído');
    $('currentStatus').textContent = active ? `${active.name} · ${Math.round(fraction * 100)}% · ${bytes(active.bytes)}${active.totalBytes ? ` / ${bytes(active.totalBytes)}` : ''}` : last ? `${last.name} · ${last.status}` : 'Nenhum download iniciado';
    $('currentSpeed').textContent = active ? `${speed(active.speed)}${active.speed > 0 && active.totalBytes > active.bytes ? ' · ' + Math.ceil((active.totalBytes - active.bytes) / active.speed / 60) + ' min restantes' : ''}` : '—';
    if (!state.jobsVisible) return;
    const box = $('jobs'); box.replaceChildren();
    if (!jobs.length) box.append(el('p', 'hint', 'Nenhum download nesta sessão.'));
    for (const item of jobs) {
      const row = el('div', 'job'); row.dataset.status = item.status;
      const cover = safeImage(item.logo);
      if (cover) {
        const poster = el('img', 'job-poster'); poster.src = cover; poster.alt = ''; poster.loading = 'lazy'; poster.referrerPolicy = 'no-referrer';
        poster.onerror = () => poster.remove(); row.append(poster);
      }
      const name = el('span', '', item.name); name.title = item.name;
      const detail = el('div', 'job-detail');
      detail.append(name, el('span', 'hint', `${item.status === 'Cancelado' ? 'Parado' : item.status} · ${Math.round((item.progress || 0) * 100)}% · ${bytes(item.bytes)}${item.totalBytes ? ' / ' + bytes(item.totalBytes) : ''}`));
      if (item.detail && ['Baixando', 'Pausado'].includes(item.status)) detail.append(el('span', 'hint', item.detail));
      if (item.error) detail.append(el('p', 'job-error', item.error));
      const bar = el('progress', 'job-progress'); bar.max = 1; bar.value = item.progress || 0;
      bar.setAttribute('aria-label', `Progresso de ${item.name}: ${item.status}`);
      detail.append(bar); row.append(detail);
      if (['Baixando', 'Na fila', 'Pausado'].includes(item.status)) {
        const controls = el('div', 'job-actions');
        for (const action of [item.status === 'Pausado' ? 'resume' : 'pause', 'stop']) {
          const label = { pause: 'Pausar', resume: 'Continuar', stop: 'Parar' }[action];
          const button = el('button', 'btn', label);
          button.onclick = async () => {
            if (action === 'stop' && !await appDialog('Parar este download? O arquivo parcial será preservado.', { title: 'Parar download', confirmLabel: 'Parar', danger: true })) return;
            button.disabled = true;
            try { await request(`/api/downloads/${encodeURIComponent(item.id)}/control`, { action }); await pollDownloads(); }
            catch (e) { toast(e.message, true); button.disabled = false; }
          };
          controls.append(button);
        }
        row.append(controls);
      }
      if (['Erro', 'Cancelado', 'Interrompido'].includes(item.status)) {
        const actions = el('div', 'job-actions');
        for (const restart of [false, true]) {
          const button = el('button', 'btn', restart ? 'Reiniciar download' : 'Tentar novamente');
          button.onclick = async () => {
            if (restart && !await appDialog('Reiniciar do zero? O arquivo anterior será guardado como backup e ocupará espaço.', { title: 'Reiniciar download', confirmLabel: 'Reiniciar', danger: true })) return;
            button.disabled = true;
            try { await request(`/api/downloads/${encodeURIComponent(item.id)}/retry`, { restart }); await pollDownloads(); }
            catch (e) { toast(e.message, true); button.disabled = false; }
          };
          actions.append(button);
        }
        const deleteButton = el('button', 'btn warn', 'Excluir');
        deleteButton.onclick = async () => deleteDownloadItem(item.id, deleteButton);
        actions.append(deleteButton);
        row.append(actions);
      }
      if (item.status === 'Concluído') {
        const doneActions = el('div', 'job-actions');
        const openButton = el('button', 'btn', 'Ver pasta');
        openButton.onclick = async () => openDownloadFolder(item.id, openButton);
        const deleteButton = el('button', 'btn warn', 'Excluir');
        deleteButton.onclick = async () => deleteDownloadItem(item.id, deleteButton);
        doneActions.append(openButton, deleteButton);
        row.append(doneActions);
      }
      box.append(row);
    }
  }
  async function pollDownloads() { try { renderJobs(await request('/api/downloads')); } catch (e) { if (!pollDownloads.offline) toast(e.message, true); pollDownloads.offline = true; return; } pollDownloads.offline = false; }
  function on(id, event, fn) { $(id).addEventListener(event, fn); }
  function showView(view) {
    $('configPanel').classList.toggle('hidden', view !== 'config');
    document.querySelector('.toolbar').classList.toggle('hidden', view !== 'catalog');
    document.body.classList.toggle('view-downloads', view === 'downloads');
    $('workspaceTabs').classList.toggle('hidden', view === 'config');
    $('viewDownloads').classList.toggle('active', view === 'downloads');
    $('moviesTab').classList.toggle('active', view === 'catalog' && state.kind === 'movies');
    $('seriesTab').classList.toggle('active', view === 'catalog' && state.kind === 'series');
    document.querySelector('.content-panel').classList.toggle('hidden', view !== 'catalog');
    document.querySelector('.downloads-panel').classList.toggle('hidden', view !== 'downloads');
    document.querySelectorAll('[data-view]').forEach(b => { b.classList.toggle('active', b.dataset.view === view); b.setAttribute('aria-current', b.dataset.view === view ? 'page' : 'false'); });
  }
  function setSidebarCollapsed(collapsed) {
    document.body.classList.toggle('sidebar-collapsed', collapsed);
    const toggle = $('sidebarToggle');
    toggle.textContent = collapsed ? '›' : '‹';
    toggle.setAttribute('aria-expanded', String(!collapsed));
    toggle.title = collapsed ? 'Expandir menu lateral' : 'Recolher menu lateral';
    toggle.setAttribute('aria-label', toggle.title);
    try { localStorage.setItem('iptv-sidebar-collapsed', String(collapsed)); } catch { /* optional preference */ }
  }
  function wireEvents() {
    let collapsed = false;
    try { collapsed = localStorage.getItem('iptv-sidebar-collapsed') === 'true'; } catch { }
    setSidebarCollapsed(collapsed);
    on('sidebarToggle', 'click', () => setSidebarCollapsed(!document.body.classList.contains('sidebar-collapsed')));
    on('dialogCancel', 'click', () => closeAppDialog(false));
    on('dialogOk', 'click', () => closeAppDialog(true));
    on('dialogCopy', 'click', async () => {
      try { await navigator.clipboard.writeText($('dialogPath').value); toast('Caminho copiado.'); }
      catch { $('dialogPath').focus(); $('dialogPath').select(); toast('Selecione e copie o caminho exibido.'); }
    });
    $('appDialog').addEventListener('click', e => { if (e.target === $('appDialog')) closeAppDialog(false); });
    document.addEventListener('keydown', e => {
      if (!$('appDialog').classList.contains('hidden')) {
        if (e.key === 'Escape') { e.preventDefault(); closeAppDialog(false); }
        if (e.key === 'Tab') { const nodes = [...$('appDialog').querySelectorAll('button:not(.hidden),input:not(.hidden)')].filter(n => n.offsetParent !== null); if (nodes.length) { const first = nodes[0], last = nodes[nodes.length-1]; if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); } else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); } } }
      }
    });
    document.querySelectorAll('[data-config]').forEach(button => button.addEventListener('click', () => {
      document.querySelectorAll('[data-config]').forEach(b => { b.classList.toggle('active', b === button); b.setAttribute('aria-selected', b === button ? 'true' : 'false'); });
      document.querySelectorAll('.tab-body').forEach(b => b.classList.toggle('hidden', b.id !== `config-${button.dataset.config}`));
    }));
    on('viewDownloads', 'click', () => showView('downloads'));
    document.querySelectorAll('[data-view]').forEach(b => b.onclick = () => showView(b.dataset.view));
    on('connectForm', 'submit', async ev => {
      ev.preventDefault(); setBusy(true, 'Conectando à lista IPTV…');
      try {
        await request('/api/connect', { server: $('server').value.trim(), username: $('username').value.trim(), password: $('password').value, remember: $('remember').checked });
        $('password').value = ''; state.lists = { movies: null, series: null }; await refreshState();
        showView('catalog');
        await loadCatalog('movies', true);
      } catch (e) { toast(e.message, true); empty(e.message, 'Falha na conexão'); }
      finally { setBusy(false); }
    });
    on('connectSaved', 'click', async () => {
      const key = $('savedAccounts').value; if (!key) { toast('Selecione uma conta salva.', true); return; }
      setBusy(true, 'Conectando à conta salva…');
      try { await request('/api/connect-saved', { key }); state.lists = { movies: null, series: null }; await refreshState(); showView('catalog'); await loadCatalog('movies', true); }
      catch (e) { toast(e.message, true); } finally { setBusy(false); }
    });
    on('refreshCatalog', 'click', () => loadCatalog(state.kind, true));
    on('settingsForm', 'submit', async ev => {
      ev.preventDefault(); try { await request('/api/settings', { downloadParent: $('downloadParent').value, hideAdult: $('hideAdult').checked, downloadConnections: Number($('downloadConnections').value) }); state.lists = { movies: null, series: null }; await refreshState(); toast('Configurações salvas.'); if (state.connected) await loadCatalog(state.kind, true); }
      catch (e) { toast(e.message, true); }
    });
    on('presetStorage', 'click', () => { $('downloadParent').value = '/storage'; $('downloadParent').focus(); });
    on('clearDownloadParent', 'click', () => { $('downloadParent').value = ''; $('downloadParent').focus(); });
    on('moviesTab', 'click', () => { showView('catalog'); loadCatalog('movies'); });
    on('seriesTab', 'click', () => { showView('catalog'); loadCatalog('series'); });
    on('search', 'input', ev => { state.search = ev.target.value; state.page = 1; render(); });
    on('category', 'change', ev => { state.category = ev.target.value; state.page = 1; render(); });
    on('back', 'click', back);
    on('prevPage', 'click', () => { state.page--; render(); $('cards').scrollTop = 0; });
    on('nextPage', 'click', () => { state.page++; render(); $('cards').scrollTop = 0; });
    on('selectVisible', 'click', () => { for (const x of visiblePage()) state.selected.add(x.id); render(); });
    on('clearSelection', 'click', () => { state.selected.clear(); render(); });
    on('downloadSelected', 'click', () => enqueue([...state.selected]));
    on('pauseDownload', 'click', async () => { try { await request(dataPaused ? '/api/downloads/resume' : '/api/downloads/pause', {}); await pollDownloads(); } catch (e) { toast(e.message, true); } });
    on('cancelDownload', 'click', async () => { if (!await appDialog('Cancelar o download em andamento? Os demais arquivos da fila continuam.', { title: 'Cancelar download', confirmLabel: 'Cancelar download', danger: true })) return; try { await request('/api/downloads/cancel', {}); await pollDownloads(); } catch (e) { toast(e.message, true); } });
    on('showJobs', 'click', () => { state.jobsVisible = !state.jobsVisible; $('jobs').classList.toggle('hidden', !state.jobsVisible); $('showJobs').textContent = state.jobsVisible ? 'Ocultar fila' : 'Mostrar fila'; pollDownloads(); });
  }
  async function init() {
    wireEvents(); render();
    try { await refreshState(); if (state.connected) await loadCatalog('movies'); else showView('config'); }
    catch (e) { toast(e.message, true); empty(e.message, 'Sem conexão com o servidor'); }
    await pollDownloads(); state.poll = window.setInterval(() => { if (!document.hidden || state.jobsVisible) pollDownloads(); }, 1300);
  }
  init();
})();
