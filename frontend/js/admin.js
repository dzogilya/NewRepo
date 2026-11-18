const $ = id => document.getElementById(id);

const SERVER_UA = {
  Free: 'вільний',
  Busy: 'рахує',
  Starting: 'запускається'
};

async function refresh() {
  const res = await api('/api/admin');
  if (res.status === 401) { location.href = '/login.html'; return; }
  if (res.status === 403) { $('error').textContent = 'Ця сторінка лише для адміністратора.'; return; }
  if (!res.ok) return;
  const d = res.data;

  const load = d.servers.length ? Math.round(100 * d.busyServers / d.servers.length) : 0;
  $('counters').innerHTML = `
    <div>Серверів<b>${d.servers.length}</b><span class="hint">максимум ${d.maxServers}</span></div>
    <div>Навантаження<b>${load}%</b><span class="hint">зайнято ${d.busyServers} з ${d.servers.length}</span></div>
    <div>У черзі<b>${d.queued}</b><span class="hint">максимум ${d.maxQueue}</span></div>
    <div>Рахуються<b>${d.running}</b></div>
    <div>Готово<b>${d.success}</b></div>
    <div>Помилок<b>${d.failed}</b></div>
    <div>Скасовано<b>${d.cancelled}</b></div>`;

  $('servers').innerHTML = d.servers.map(s => `<tr>
    <td>${esc(s.id)}</td>
    <td>${s.port}</td>
    <td>${s.pid}</td>
    <td>${s.isExtra ? 'додатковий' : 'базовий'}</td>
    <td class="server-${s.status}">${SERVER_UA[s.status]}</td>
    <td>${s.currentTaskId ? '#' + s.currentTaskId : '—'}</td>
    <td>${s.tasksDone}</td>
    <td>${formatTime(s.startedAt)}</td>
  </tr>`).join('');

  $('tasks').innerHTML = d.tasks.map(t => `<tr>
    <td>${t.id}</td>
    <td>${esc(t.username)}</td>
    <td>${Number(t.cost).toLocaleString('uk-UA')}</td>
    <td class="status-${t.status}">${STATUS_UA[t.status]}</td>
    <td>${t.progress}%</td>
    <td>${esc(t.serverId || '')}</td>
    <td>${formatTime(t.createdAt)}</td>
    <td>${t.status === 'Queued' || t.status === 'Running'
      ? `<button class="small" data-cancel="${t.id}">Скасувати</button>` : ''}</td>
  </tr>`).join('');
}

$('tasks').onclick = async e => {
  const id = e.target.dataset.cancel;
  if (id) {
    await api(`/api/admin/tasks/${id}/cancel`, 'POST');
    refresh();
  }
};

refresh();
setInterval(refresh, 2000);
