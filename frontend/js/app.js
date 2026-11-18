const $ = id => document.getElementById(id);
let shownResultId = null;

async function init() {
  const me = await api('/api/auth/me');
  if (me.status === 401) { location.href = '/login.html'; return; }
  if (!me.ok) { setTimeout(init, 1000); return; }
  $('userName').textContent = me.data.username;
  if (me.data.role === 'admin') $('adminLink').classList.remove('hidden');

  $('startBtn').onclick = sendTask;
  $('tasks').onclick = onTaskButton;

  refresh();
  setInterval(refresh, 1000);
}

function validateInputs(size, iterations, top, bottom, left, right) {
  if (isNaN(size) || size < 10 || size > 1000) return 'Розмір сітки N має бути від 10 до 1000.';
  if (isNaN(iterations) || iterations < 10 || iterations > 50000) return 'Кількість ітерацій K має бути від 10 до 50 000.';
  const temps = [top, bottom, left, right];
  for (const t of temps) {
    if (isNaN(t) || t < -273 || t > 5000) return 'Температури країв мають бути від -273 до 5000 °C.';
  }
  const cost = size * size * iterations;
  if (cost > 20000000000) return `Задача завелика: N·N·K = ${cost.toLocaleString('uk-UA')}, максимум 20 000 000 000.`;
  return null;
}

async function sendTask() {
  $('formError').textContent = '';
  $('formInfo').textContent = '';

  const body = {};
  for (const f of ['size', 'iterations', 'top', 'bottom', 'left', 'right']) body[f] = +$(f).value;

  const clientError = validateInputs(body.size, body.iterations, body.top, body.bottom, body.left, body.right);
  if (clientError) {
    $('formError').textContent = clientError;
    return;
  }

  const res = await api('/api/tasks', 'POST', body);
  if (res.ok) $('formInfo').textContent = res.data.message;
  else $('formError').textContent = res.data?.message || `Помилка ${res.status}`;
  refresh();
}

async function refresh() {
  const res = await api('/api/tasks');
  if (res.status === 401) { location.href = '/login.html'; return; }
  if (!res.ok) return;

  const tasks = res.data;
  $('noTasks').classList.toggle('hidden', tasks.length > 0);
  $('tasks').innerHTML = tasks.map(rowHtml).join('');
}

function rowHtml(t) {
  const p = t.params;
  let where = '';
  if (t.status === 'Queued') where = `позиція ${t.queuePosition}, очікування ${formatWait(t.waitSeconds)}`;
  else if (t.status === 'Running') where = `${esc(t.serverId)}, ітерація ${t.iteration} / ${p.iterations}`;
  else if (t.status === 'Failed') where = `<span class="error">${esc(t.error)}</span>`;
  else if (t.finishedAt) where = `завершено о ${formatTime(t.finishedAt)}`;

  let buttons = '';
  if (t.status === 'Queued' || t.status === 'Running') {
    buttons = `<button class="small" data-cancel="${t.id}">Скасувати</button>`;
  } else {
    if (t.status === 'Success') buttons += `<button class="small" data-view="${t.id}">Показати</button> `;
    buttons += `<button class="small" data-delete="${t.id}">Видалити</button>`;
  }

  return `<tr>
    <td>${t.id}</td>
    <td>${p.size}×${p.size}</td>
    <td>${p.iterations}</td>
    <td class="status-${t.status}">${STATUS_UA[t.status]}</td>
    <td><div class="bar"><div style="width:${t.progress}%"></div></div>${t.progress}%</td>
    <td>${where}</td>
    <td>${buttons}</td>
  </tr>`;
}

async function onTaskButton(e) {
  const b = e.target.dataset;
  if (b.cancel) await api(`/api/tasks/${b.cancel}/cancel`, 'POST');
  if (b.delete) {
    await api(`/api/tasks/${b.delete}`, 'DELETE');
    if (shownResultId === +b.delete) $('resultCard').classList.add('hidden');
  }
  if (b.view) await showResult(+b.view);
  refresh();
}

async function showResult(id) {
  const res = await api(`/api/tasks/${id}/result`);
  if (!res.ok) return;
  const r = res.data;
  shownResultId = id;

  drawHeatmap($('resultCanvas'), r.map, r.mapSize, r.min, r.max);
  drawLegend($('legend'));
  $('legendMin').textContent = `${r.min} °C`;
  $('legendMax').textContent = `${r.max} °C`;
  $('resultTitle').textContent = `Результат задачі #${id}`;
  $('resultStats').innerHTML = `
    <p>Виконано ітерацій: <b>${r.iterations}</b></p>
    <p>Збіжність досягнута: <b>${r.converged ? 'так' : 'ні (вичерпано K)'}</b></p>
    ${r.converged ? '' : '<p class="hint">Тепло ще не встигло поширитися по всій пластині.<br>Для точнішого результату збільште K або зменшіть N.</p>'}
    <p>Мінімум / максимум: <b>${r.min} / ${r.max} °C</b></p>
    <p>Час обчислення: <b>${r.seconds} с</b></p>`;
  $('resultCard').classList.remove('hidden');
}

init();