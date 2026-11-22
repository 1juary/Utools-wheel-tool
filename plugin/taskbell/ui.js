(function () {
const el=id=>document.getElementById(id)
let editing=null
let listRequest=0
let tasks=[]
const deleting=new Set()
function localTime(date){return new Date(date-date.getTimezoneOffset()*60000).toISOString().slice(0,16)}
function reset(){editing=null;el('entry').reset();el('name').focus()}
async function run(fn){try{el('msg').textContent='';await fn()}catch(e){el('msg').textContent=e.message}}
async function list(){
 const request=++listRequest
 const latest=JSON.parse(await bell('List'))
 if(request!==listRequest)return
 tasks=latest
 renderTasks()
}
async function deleteTask(id){
 if(deleting.has(id))return
 deleting.add(id)
 renderTasks()
 try{
  await bell('Delete',{id})
  if(editing?.id===id)reset()
  tasks=tasks.filter(task=>task.id!==id)
  renderTasks()
  await list()
 }finally{
  deleting.delete(id)
  renderTasks()
 }
}
function renderTasks(){
 el('rows').replaceChildren()
 for(const t of tasks.sort((a,b)=>new Date(a.remindAt)-new Date(b.remindAt))){
  const row=document.createElement('article'), title=document.createElement('span'), info=document.createElement('small')
  title.textContent=t.title
  info.textContent=new Date(t.remindAt).toLocaleString()+' · '+(t.status==='done'?'已完成':t.silent?'已静音，仍待办':'待提醒')
  title.append(info);row.append(title)
  function button(label,fn){const b=document.createElement('button');b.textContent=label;b.disabled=deleting.has(t.id);b.onclick=()=>run(fn);row.append(b)}
  button(t.status==='done'?'重新提醒':'编辑',()=>{editing=t;el('name').value=t.title;el('at').value=localTime(new Date(t.remindAt));el('name').focus()})
  if(t.status!=='done')button('完成',async()=>{await bell('Done',{id:t.id});await list()})
  button(deleting.has(t.id)?'删除中…':'删除',()=>deleteTask(t.id))
  el('rows').append(row)
 }
}
el('minute').onclick=()=>{el('at').step=1;const d=new Date(Date.now()+60000);el('at').value=localTime(d)+':'+String(d.getSeconds()).padStart(2,'0')}
el('reset').onclick=reset
el('refresh').onclick=()=>run(list)
el('entry').onsubmit=e=>{e.preventDefault();run(async()=>{await bell('Save',{id:editing?.id,reopen:editing?.status==='done',title:el('name').value,remindAt:new Date(el('at').value).toISOString()});reset();await list()})}
let watching = false
let installed = false

window.showWheelReminders = title => {
 document.getElementById('wheel-settings').hidden = true
 el('wheel-reminders').hidden = false
 reset()
 if (title) {
  el('name').value = title
  el('at').focus()
 }
 return run(async () => {
  if (!watching) {
   onBellTasksChanged(() => list().catch(e => { el('msg').textContent = e.message }))
   watching = true
  }
  await list()
  if (!installed) {
   await bell('Install')
   installed = true
  }
 })
}

el('back-to-wheel').onclick = () => {
 el('wheel-reminders').hidden = true
 el('wheel-settings').hidden = false
}
})();
