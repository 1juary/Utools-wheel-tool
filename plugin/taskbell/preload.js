const fs = require('fs')
const path = require('path')
const {execFile} = require('child_process')
const root = path.join(process.env.LOCALAPPDATA, 'TaskBell')

const script = path.join(root, 'Bell.ps1')
const records = path.join(root, 'tasks')
let deployed = false

function deploy() {
  if (deployed) return
  fs.mkdirSync(records, {recursive:true})
  for (const name of ['Reminder.vbs','bell.wav','Bell.ps1']) {
    const target = path.join(root, name)
    const content = fs.readFileSync(path.join(__dirname, name))
    if (fs.existsSync(target) && fs.readFileSync(target).equals(content)) continue
    const temporary = target + '.' + process.pid + '.tmp'
    fs.writeFileSync(temporary, content)
    fs.renameSync(temporary, target)
  }
  deployed = true
}

window.onBellTasksChanged = callback => {
  deploy()
  let timer
  const watcher = fs.watch(records, (_, name) => {
    if (name && !name.endsWith('.json')) return
    clearTimeout(timer)
    timer = setTimeout(callback, 120)
  })
  window.addEventListener('unload', () => {
    clearTimeout(timer)
    watcher.close()
  }, {once:true})
}
async function listRecords() {
  const names = await fs.promises.readdir(records)
  const tasks = await Promise.all(names.filter(name => name.endsWith('.json')).map(async name => {
    try {
      const content = await fs.promises.readFile(path.join(records, name), 'utf8')
      return JSON.parse(content.replace(/^\uFEFF/, ''))
    } catch (error) {
      if (error.code === 'ENOENT') return null
      throw error
    }
  }))
  return JSON.stringify(tasks.filter(Boolean))
}

window.bell = (action, data={}) => new Promise((resolve,reject)=>{
  deploy()
  if (action === 'List') return resolve(listRecords())
  const payload = Buffer.from(JSON.stringify(data)).toString('base64')
  execFile(path.join(process.env.SystemRoot,'System32/WindowsPowerShell/v1.0/powershell.exe'),
    ['-NoProfile','-ExecutionPolicy','Bypass','-File',script,action,payload],
    {windowsHide:true,encoding:'utf8'},(err,out,stderr)=>err?reject(new Error(stderr||err.message)):resolve(out.trim()))
})

module.exports = { deploy }
