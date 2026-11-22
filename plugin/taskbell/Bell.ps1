param([ValidateSet('Install','Recover','List','Save','Done','Delete','Alarm','Check')]$Action='List',[string]$Payload)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$dir=Join-Path $PSScriptRoot 'tasks'
[IO.Directory]::CreateDirectory($dir) | Out-Null
$user=[Security.Principal.WindowsIdentity]::GetCurrent()
$prefix="TaskBell-$($user.User.Value)-"
function Items { Get-ChildItem -LiteralPath $dir -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } }
function Store($t){
  $file=Join-Path $dir ($t.id+'.json')
  $tmp=$file+'.tmp'
  [IO.File]::WriteAllText($tmp,($t|ConvertTo-Json))
  if([IO.File]::Exists($file)){[IO.File]::Replace($tmp,$file,$file+'.bak')}else{[IO.File]::Move($tmp,$file)}
}
function Schedule($key,$trigger,$mode){
  $a=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\wscript.exe" -Argument ('//B //Nologo "'+(Join-Path $PSScriptRoot 'Reminder.vbs')+'" '+$mode)
  $s=New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([timespan]::Zero)
  Register-ScheduledTask -TaskName ($prefix+$key) -Action $a -Trigger $trigger -Settings $s -User $user.Name -Force | Out-Null
}
function Arm($t){Schedule $t.id (New-ScheduledTaskTrigger -Once -At ([datetimeoffset]::Parse($t.remindAt).LocalDateTime)) Alarm}
function Disarm($id){
  if(Get-ScheduledTask -TaskName ($prefix+$id) -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName ($prefix+$id) -Confirm:$false}
}
function Due { Items | Where-Object {$_.status -eq 'pending' -and !$_.silent -and [datetimeoffset]::Parse($_.remindAt) -le [datetimeoffset]::Now} }
if($Payload){$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Payload))|ConvertFrom-Json}
$lock=New-Object Threading.Mutex($false,($prefix+'store'))
function Locked([scriptblock]$body){
  try{$null=$lock.WaitOne()}catch [Threading.AbandonedMutexException]{}
  try{& $body}finally{$lock.ReleaseMutex()}
}
if($Action -eq 'Check'){
  $player=New-Object Media.SoundPlayer((Join-Path $PSScriptRoot 'bell.wav'))
  $player.Load()
  Get-Command Register-ScheduledTask | Out-Null
  'OK';return
}
if($Action -eq 'List'){Locked {ConvertTo-Json -InputObject @(Items) -Compress};return}
if($Action -in 'Save','Done','Delete'){
  Locked {
    $id=if($p.id){([guid]$p.id).ToString()}else{[guid]::NewGuid().ToString()}
    $t=Items | Where-Object id -eq $id
    if($p.id -and !$t){
      if($Action -eq 'Delete'){return}
      throw '任务已删除'
    }
    switch($Action){
      Save {
        if($t.status -eq 'done' -and $p.reopen -ne $true){throw '任务已完成。要再次提醒，请在清单中选择“重新提醒”。'}
        if(!$p.title.Trim() -or [datetimeoffset]::Parse($p.remindAt) -le [datetimeoffset]::Now){throw '请输入标题和未来的提醒时间'}
        $t=[pscustomobject]@{id=$id;title=$p.title.Trim();remindAt=$p.remindAt;status='pending';silent=$false}
        Arm $t;Store $t
      }
      Done {$t.status='done';Store $t;Disarm $id}
      Delete {Disarm $id;[IO.File]::Delete((Join-Path $dir ($id+'.json')))}
    }
  };return
}
if($Action -in 'Install','Recover'){
  Locked {
    if($Action -eq 'Install'){Schedule 'Login' (New-ScheduledTaskTrigger -AtLogOn -User $user.Name) Recover}
    foreach($t in Items){if($t.status -eq 'pending' -and !$t.silent -and [datetimeoffset]::Parse($t.remindAt) -gt [datetimeoffset]::Now){Arm $t}}
  }
  if(!(Due)){return}
  if($Action -eq 'Install'){Start-Process "$PSHOME\powershell.exe" -WindowStyle Hidden -ArgumentList ('-NoProfile -STA -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" Alarm');return}
}
$single=New-Object Threading.Mutex($false,($prefix+'alarm'))
try{$owner=$single.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owner=$true}
if(!$owner){return}
try{
  Add-Type -AssemblyName System.Windows.Forms
  [Windows.Forms.Application]::EnableVisualStyles()
  # Hidden process startup also hides the first WinForms window.
  # Show the reminder explicitly after its initial show has been processed.
  Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TaskBellWindow {
  [DllImport("user32.dll")]
  public static extern bool ShowWindow(IntPtr window, int command);
}
"@
  $form=New-Object Windows.Forms.Form
  $form.Text='TaskBell · 到时提醒'
  $form.TopMost=$true
  $form.Font=New-Object Drawing.Font('Microsoft YaHei UI',10)
  $form.AutoScaleMode='Dpi'
  $form.ClientSize=New-Object Drawing.Size(560,420)
  $form.Padding=New-Object Windows.Forms.Padding(24)
  $form.BackColor=[Drawing.ColorTranslator]::FromHtml('#F5F8F7')
  $form.ForeColor=[Drawing.ColorTranslator]::FromHtml('#193638')
  $form.FormBorderStyle='FixedDialog'
  $form.StartPosition='CenterScreen'
  $form.Add_Shown({
    $null=[TaskBellWindow]::ShowWindow($form.Handle,5)
    $form.Activate()
  })
  $list=New-Object Windows.Forms.ListBox
  $list.Dock='Fill';$list.DisplayMember='title';$list.BorderStyle='None'
  $list.IntegralHeight=$false
  $list.DrawMode='OwnerDrawVariable'
  $list.BackColor=[Drawing.Color]::White
  $titleFont=New-Object Drawing.Font('Microsoft YaHei UI',12,[Drawing.FontStyle]::Bold)
  $timeFont=New-Object Drawing.Font('Microsoft YaHei UI',9)
  $textFlags=[Windows.Forms.TextFormatFlags]::WordBreak -bor [Windows.Forms.TextFormatFlags]::NoPrefix
  $list.Add_MeasureItem({
    if($_.Index -lt 0){return}
    $width=[Math]::Max(80,$list.ClientSize.Width-48-[Windows.Forms.SystemInformation]::VerticalScrollBarWidth)
    $size=[Windows.Forms.TextRenderer]::MeasureText([string]$list.Items[$_.Index].title,$titleFont,(New-Object Drawing.Size($width,10000)),$textFlags)
    $_.ItemHeight=[Math]::Max(86,$size.Height+52)
  })
  $list.Add_DrawItem({
    if($_.Index -lt 0){return}
    $item=$list.Items[$_.Index]
    $bounds=$_.Bounds
    $selected=($_.State -band [Windows.Forms.DrawItemState]::Selected) -ne 0
    $background=if($selected){'#E2F1EC'}else{'#FFFFFF'}
    $brush=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml($background))
    try{$_.Graphics.FillRectangle($brush,$bounds)}finally{$brush.Dispose()}
    if($selected){
      $accent=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#19645E'))
      try{$_.Graphics.FillRectangle($accent,$bounds.X,$bounds.Y+12,3,$bounds.Height-24)}finally{$accent.Dispose()}
    }
    $titleBounds=New-Object Drawing.Rectangle(($bounds.X+20),($bounds.Y+14),($bounds.Width-48),($bounds.Height-48))
    [Windows.Forms.TextRenderer]::DrawText($_.Graphics,[string]$item.title,$titleFont,$titleBounds,$form.ForeColor,$textFlags)
    $time=[datetimeoffset]::Parse($item.remindAt).LocalDateTime.ToString('MM月dd日  HH:mm')
    $timeBounds=New-Object Drawing.Rectangle(($bounds.X+20),($bounds.Bottom-30),($bounds.Width-40),22)
    [Windows.Forms.TextRenderer]::DrawText($_.Graphics,$time,$timeFont,$timeBounds,[Drawing.ColorTranslator]::FromHtml('#526765'),[Windows.Forms.TextFormatFlags]::Left)
    if($_.State -band [Windows.Forms.DrawItemState]::Focus){$_.DrawFocusRectangle()}
  })
  $header=New-Object Windows.Forms.Panel
  $header.Dock='Top';$header.Height=88
  $heading=New-Object Windows.Forms.Label
  $heading.Text='到时间了'
  $heading.Font=New-Object Drawing.Font('Microsoft YaHei UI',22,[Drawing.FontStyle]::Bold)
  $heading.AutoSize=$true
  $summary=New-Object Windows.Forms.Label
  $summary.AutoSize=$true;$summary.Top=48
  $summary.ForeColor=[Drawing.ColorTranslator]::FromHtml('#526765')
  $header.Controls.Add($heading);$header.Controls.Add($summary)
  $bar=New-Object Windows.Forms.FlowLayoutPanel
  $bar.Dock='Bottom';$bar.Height=72
  $bar.Padding=New-Object Windows.Forms.Padding(0,24,0,0)
  $bar.WrapContents=$false
  $form.Controls.Add($list);$form.Controls.Add($bar);$form.Controls.Add($header)
  function RefreshList {
    $old=$list.SelectedItem.id
    $top=$list.TopIndex
    $list.BeginUpdate()
    try{
      $list.Items.Clear()
      foreach($item in @(Locked {Due})){ $n=$list.Items.Add($item);if($item.id -eq $old){$list.SelectedIndex=$n}}
      if($list.Items.Count -and $list.SelectedIndex -lt 0){$list.SelectedIndex=0}
      if($list.Items.Count){$list.TopIndex=[Math]::Min($top,$list.Items.Count-1)}
      $summary.Text=if($list.Items.Count -gt 1){"$($list.Items.Count) 项待处理 · 请选择要处理的任务"}else{'1 项待处理'}
      $height=if($list.Items.Count -eq 1){[Math]::Min(520,[Math]::Max(300,$list.GetItemHeight(0)+208))}else{420}
      $form.ClientSize=New-Object Drawing.Size(560,$height)
    }finally{$list.EndUpdate()}
  }
  RefreshList
  if(!$list.Items.Count){return}
  foreach($label in '完成任务','延迟10分钟','关闭本次响铃'){
    $b=New-Object Windows.Forms.Button
    $b.Text=$label
    $b.Size=New-Object Drawing.Size(164,44)
    $b.Margin=New-Object Windows.Forms.Padding(0,0,10,0)
    $b.FlatStyle='Flat'
    $b.FlatAppearance.BorderSize=0
    $b.Cursor=[Windows.Forms.Cursors]::Hand
    $b.BackColor=[Drawing.ColorTranslator]::FromHtml('#E5ECE9')
    $b.ForeColor=$form.ForeColor
    if($label -eq '完成任务'){
      $b.BackColor=[Drawing.ColorTranslator]::FromHtml('#19645E')
      $b.ForeColor=[Drawing.Color]::White
    }
    $b.Add_Click({
      $selected=$list.SelectedItem
      if(!$selected){return}
      $choice=$this.Text
      try{Locked {
        $t=Due | Where-Object id -eq $selected.id
        if(!$t){return}
        switch($choice){
          '延迟10分钟' {$t.remindAt=[datetimeoffset]::Now.AddMinutes(10).ToString('o');Arm $t}
          '完成任务' {$t.status='done'}
          '关闭本次响铃' {$t.silent=$true}
        }
        Store $t
      };RefreshList;if(!$list.Items.Count){$form.Close()}}
      catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message)}
    });$bar.Controls.Add($b)
  }
  $form.ControlBox=$false
  $form.Add_FormClosing({if($_.CloseReason -eq 'UserClosing' -and $list.Items.Count){$_.Cancel=$true}})
  $timer=New-Object Windows.Forms.Timer
  $timer.Interval=1500;$timer.Add_Tick({RefreshList;if(!$list.Items.Count){$form.Close()}})
  $sound=New-Object Media.SoundPlayer((Join-Path $PSScriptRoot 'bell.wav'))
  $sound.Load();$sound.PlayLooping();$timer.Start()
  try{$null=$form.ShowDialog()}finally{$timer.Stop();$sound.Stop();$form.Dispose();$titleFont.Dispose();$timeFont.Dispose()}
}finally{$single.ReleaseMutex()}
