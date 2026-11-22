const byId = id => document.getElementById(id);
const api = window.wheelSettings;
const startup = byId('startup');
const status = byId('status');
let manual = [];
let wheelOpen = false;

function render() {
  const list = byId('folders');
  list.replaceChildren();
  for (const folder of manual) {
    const row = document.createElement('li');
    const label = document.createElement('span');
    label.textContent = folder;
    const remove = document.createElement('button');
    remove.textContent = '移除';
    remove.onclick = () => report(() => update(manual.filter(item => item !== folder)));
    row.append(label, remove);
    list.append(row);
  }
  byId('add').disabled = manual.length >= 6;
}

function update(next) {
  api.save(next);
  manual = next;
  render();
  status.textContent = '轮盘目录已更新。';
}

async function report(action) {
  try { await action(); }
  catch (error) { status.textContent = error.message || String(error); }
}

byId('add').onclick = () => report(() => {
  const chosen = window.utools.showOpenDialog({ properties: ['openDirectory'] });
  if (chosen?.length && !manual.some(item => item.toLowerCase() === chosen[0].toLowerCase())) {
    update([...manual, chosen[0]]);
  }
});

byId('start').onclick = () => report(async () => {
  await api.control('start');
  status.textContent = '已发送启动请求。';
});
byId('stop').onclick = () => report(async () => {
  await api.control('stop');
  status.textContent = '已发送停止请求。';
});
byId('open-reminders').onclick = () => report(() => window.showWheelReminders(''));
startup.onchange = () => report(async () => {
  startup.disabled = true;
  try {
    await api.control(startup.checked ? 'enable' : 'disable');
    status.textContent = startup.checked ? '已开启登录自启。' : '已关闭登录自启。';
  } finally {
    startup.checked = api.read().startup;
    startup.disabled = false;
  }
});

report(() => {
  const settings = api.read();
  manual = settings.manual;
  startup.checked = settings.startup;
  render();
});

function showSettings() {
  byId('wheel-reminders').hidden = true;
  byId('wheel-settings').hidden = false;
}

window.utools.onPluginEnter(({ code, type, payload }) => {
  if (!code || code === 'wheel-settings') {
    showSettings();
    return;
  }
  if (code === 'text-wheel') {
    const title = type === 'over' && typeof payload === 'string' ? payload.trim() : '';
    report(() => window.showWheelReminders(title));
    return;
  }
  if (code !== 'file-wheel' || wheelOpen) return;
  wheelOpen = true;
  report(async () => {
    try {
      window.utools.hideMainWindow();
      const files = Array.isArray(payload) ? payload.map(item => item?.path).filter(path => typeof path === 'string' && path) : [];
      await api.chooseFolder(files);
    } catch (error) {
      showSettings();
      window.utools.showMainWindow();
      throw error;
    } finally {
      wheelOpen = false;
    }
  });
});
