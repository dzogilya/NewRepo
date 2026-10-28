// Admin Dashboard Script (ES Module)

document.addEventListener('DOMContentLoaded', async () => {
  window.cancelAdminTask = cancelAdminTask;
  await checkAdminAuth();
  loadAllData();
  setInterval(loadAllData, 3000); // 3-second live refresh
});

async function checkAdminAuth() {
  try {
    const res = await fetch('/api/auth/me');
    if (!res.ok) {
      window.location.href = '/';
      return;
    }
    const user = await res.json();
    if (user.role !== 'admin') {
      alert("Доступ дозволено виключно адміністраторам.");
      window.location.href = '/';
      return;
    }
    document.getElementById('adminName').textContent = `Адміністратор: ${user.username}`;
  } catch {
    window.location.href = '/';
  }
}

async function loadAllData() {
  await Promise.all([
    loadMetrics(),
    loadServers(),
    loadScalingEvents(),
    loadAllTasks()
  ]);
}

async function loadMetrics() {
  try {
    const res = await fetch('/api/admin/metrics');
    if (res.ok) {
      const data = await res.json();
      document.getElementById('metricQueued').textContent = data.queuedCount;
      document.getElementById('metricRunning').textContent = data.runningCount;
      document.getElementById('metricSuccess').textContent = data.successCount;
      document.getElementById('metricFailed').textContent = data.failedCount;
      document.getElementById('metricServers').textContent = data.activeAppServers;
    }
  } catch (err) {
    console.error(err);
  }
}

async function loadServers() {
  try {
    const res = await fetch('/api/admin/servers');
    if (res.ok) {
      const servers = await res.json();
      const tbody = document.getElementById('serversTableBody');

      if (!servers || servers.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="text-align: center; color: var(--text-muted);">Немає підключених серверів.</td></tr>';
        return;
      }

      tbody.innerHTML = servers.map(s => `
        <tr>
          <td><strong>${s.serverId}</strong></td>
          <td>${s.serverType || 'AppServer'}</td>
          <td><span class="badge badge-success">${s.status || 'Alive'}</span></td>
          <td>${s.activeTasks ?? 0}</td>
          <td>${s.ramUsageMb ?? '—'} MB</td>
          <td>${s.lastHeartbeat ? new Date(s.lastHeartbeat).toLocaleTimeString('uk-UA') : '—'}</td>
        </tr>
      `).join('');
    }
  } catch (err) {
    console.error(err);
  }
}

async function loadScalingEvents() {
  try {
    const res = await fetch('/api/admin/scaling-events');
    if (res.ok) {
      const events = await res.json();
      const tbody = document.getElementById('scalingTableBody');

      if (!events || events.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" style="text-align: center; color: var(--text-muted);">Немає зафіксованих подій масштабування.</td></tr>';
        return;
      }

      tbody.innerHTML = events.map(e => `
        <tr>
          <td>${new Date(e.timestamp).toLocaleString('uk-UA')}</td>
          <td><span class="badge ${e.action === 'SCALE_UP' ? 'badge-running' : 'badge-queued'}">${e.action}</span></td>
          <td>${e.reason}</td>
          <td>${e.serverCountBefore}</td>
          <td><strong>${e.serverCountAfter}</strong></td>
        </tr>
      `).join('');
    }
  } catch (err) {
    console.error(err);
  }
}

async function loadAllTasks() {
  try {
    const res = await fetch('/api/admin/tasks?pageSize=15');
    if (res.ok) {
      const data = await res.json();
      const tbody = document.getElementById('allTasksTableBody');

      if (!data.items || data.items.length === 0) {
        tbody.innerHTML = '<tr><td colspan="8" style="text-align: center; color: var(--text-muted);">Немає задач.</td></tr>';
        return;
      }

      tbody.innerHTML = data.items.map(t => `
        <tr>
          <td style="font-family: monospace; font-size: 0.75rem;">${t.id.substring(0, 8)}...</td>
          <td>${t.username}</td>
          <td>${t.type}</td>
          <td><span class="badge badge-${t.status.toLowerCase()}">${t.stage}</span></td>
          <td>${t.progress}%</td>
          <td>${t.appServerId || '—'}</td>
          <td>${new Date(t.createdAt).toLocaleTimeString('uk-UA')}</td>
          <td>
            ${t.status === 'Running' || t.status === 'Queued'
              ? `<button class="btn btn-danger" style="padding: 0.25rem 0.5rem; font-size: 0.75rem;" onclick="cancelAdminTask('${t.id}')">Зупинити</button>`
              : '—'}
          </td>
        </tr>
      `).join('');
    }
  } catch (err) {
    console.error(err);
  }
}

async function cancelAdminTask(taskId) {
  if (!confirm(`Примусово скасувати задачу ${taskId.substring(0, 8)}?`)) return;

  try {
    const res = await fetch(`/api/admin/tasks/${taskId}/cancel`, { method: 'POST' });
    if (res.ok) {
      loadAllData();
    } else {
      alert("Не вдалося скасувати задачу.");
    }
  } catch (err) {
    alert("Помилка запиту.");
  }
}
