const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { spawn } = require('node:child_process');
const { promisify } = require('node:util');
const { execFile } = require('node:child_process');
const { deploy: deployReminders } = require('./taskbell/preload.js');

const run = promisify(execFile);
const home = path.join(process.env.LOCALAPPDATA, 'UToolsWheel');
const settingsFile = path.join(home, 'settings.json');
const startupLink = path.join(process.env.APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Startup', 'UToolsWheel.lnk');

function installedHelper() {
  const content = fs.readFileSync(path.join(__dirname, 'Wheel.exe'));
  const version = createHash('sha256').update(content).digest('hex').slice(0, 16);
  const executable = path.join(home, 'Wheel-' + version + '.exe');
  fs.mkdirSync(home, { recursive: true });
  if (!fs.existsSync(executable)) {
    const temporary = executable + '.' + process.pid + '.tmp';
    fs.writeFileSync(temporary, content);
    fs.renameSync(temporary, executable);
  }
  return executable;
}

function start(executable) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, [], { detached: true, stdio: 'ignore', windowsHide: true });
    child.once('error', reject);
    child.once('spawn', () => {
      child.unref();
      resolve();
    });
  });
}

window.wheelSettings = {
  async chooseFolder(files) {
    const executable = installedHelper();
    const args = ['--file-wheel'];
    let directory;
    let request;
    try {
      if (files.length) {
        directory = fs.mkdtempSync(path.join(home, 'selection-'));
        request = path.join(directory, 'files.json');
        fs.writeFileSync(request, JSON.stringify(files));
        args.push(request);
      }
      await run(executable, args, { windowsHide: true });
    } catch (error) {
      if (error.code === 11) throw new Error('暂时没有可用目录，请在「轮盘设置」添加文件夹。');
      throw error;
    } finally {
      if (request && fs.existsSync(request)) fs.unlinkSync(request);
      if (directory) fs.rmdirSync(directory);
    }
  },
  read() {
    let manual = [];
    try {
      const data = JSON.parse(fs.readFileSync(settingsFile, 'utf8'));
      if (Array.isArray(data.Manual)) manual = data.Manual.filter(item => typeof item === 'string');
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    return { manual, startup: fs.existsSync(startupLink) };
  },
  save(manual) {
    fs.mkdirSync(home, { recursive: true });
    const temporary = settingsFile + '.' + process.pid + '.tmp';
    fs.writeFileSync(temporary, JSON.stringify({ Manual: manual }));
    fs.renameSync(temporary, settingsFile);
  },
  async control(action) {
    const executable = installedHelper();
    if (action === 'start') {
      deployReminders();
      return start(executable);
    }
    if (action === 'stop') return run(executable, ['--stop'], { windowsHide: true });
    if (action === 'enable') {
      deployReminders();
      await run(executable, ['--enable-startup'], { windowsHide: true });
      return start(executable);
    }
    if (action === 'disable') return run(executable, ['--disable-startup'], { windowsHide: true });
    throw new Error('未知操作');
  }
};
