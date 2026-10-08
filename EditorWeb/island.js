(() => {
  'use strict';
  const send = message => window.chrome?.webview?.postMessage(message);
  const state = document.getElementById('state');
  const selectionTools = document.getElementById('selection-tools');
  const slashMenu = document.getElementById('slash-menu');
  let editor, session = null, loading = false, composing = false, pendingLoad = null;
  let ready = false, selectedRange = null, slashBlock = null, slashIndex = 0, slashItems = [];
  let imageRequest = null;
  let sticky = false, readOnly = false, savingReadOnly = false;
  let writingOnly = false, saveAfterComposition = false;
  function saveNow() {
    if (!writingOnly || !session?.taskId) return;
    if (composing) { saveAfterComposition = true; return; }
    flush(); send({kind:'saveNow',token:session.token});
  }
  const textKey = text => text.replace(/\s+/g, ' ').trim().slice(0, 512);
  function readingBlocks() {
    const root = editor.vditor.wysiwyg.element;
    const selector = 'p,h1,h2,h3,h4,h5,h6,pre:not(.vditor-reset),li,td,th';
    return [...root.querySelectorAll(selector)].filter(node => !node.querySelector(selector) && !node.closest('[data-render="2"]'));
  }
  function readingPosition() {
    if (!ready || !session) return null;
    const root = editor.vditor.wysiwyg.element, blocks = readingBlocks();
    const top = root.getBoundingClientRect().top + 14;
    let index = 0;
    blocks.forEach((node, i) => { if (node.getBoundingClientRect().top <= top) index = i; });
    const node = blocks[index]; if (!node) return null;
    const rect = node.getBoundingClientRect(), text = textKey(node.textContent);
    return {text, index, occurrence:blocks.slice(0,index).filter(n=>textKey(n.textContent)===text).length,
      fraction:Math.max(0, Math.min(1, (top-rect.top)/Math.max(1,rect.height))),
      ratio:root.scrollTop/Math.max(1,root.scrollHeight-root.clientHeight)};
  }
  function restorePosition(position) {
    if (!position || !ready) return;
    const root = editor.vditor.wysiwyg.element, blocks = readingBlocks();
    const text = position.text ?? position.Text, occurrence = position.occurrence ?? position.Occurrence;
    const node = blocks.filter(n=>textKey(n.textContent)===text)[occurrence] ?? blocks[position.index ?? position.Index];
    if (node) {
      const rect = node.getBoundingClientRect();
      root.scrollTop += rect.top - root.getBoundingClientRect().top - 14 + rect.height * (position.fraction ?? position.Fraction);
    } else root.scrollTop = (position.ratio ?? position.Ratio) * Math.max(0,root.scrollHeight-root.clientHeight);
    queueOutlinePosition();
  }
  function applyReadState() {
    if (readOnly || savingReadOnly || !session?.taskId) editor.disabled(); else editor.enable();
    document.body.classList.toggle('note-readonly', readOnly);
    document.getElementById('finish').hidden = sticky || writingOnly;
    document.getElementById('source').hidden = writingOnly;
    document.getElementById('source').title = sticky ? '编辑 Markdown 原文；Ctrl+S 保存后阅读' : '编辑 Markdown 原文';
    state.textContent = savingReadOnly ? '正在保存…' : readOnly ? '只读 · 点击正文编辑' : sticky ? '自动保存 · Ctrl+S 阅读' : '自动保存';
    hideMenus();
  }
  async function preparePosition(position, request) {
    const token = session?.token;
    restorePosition(position);
    const root = editor.vditor.wysiwyg.element, viewport = root.getBoundingClientRect();
    const visibleImages = [...root.querySelectorAll('img')].filter(img => {
      const rect = img.getBoundingClientRect(); return rect.bottom >= viewport.top-100 && rect.top <= viewport.bottom+100;
    });
    let timeout;
    await Promise.race([
      Promise.all(visibleImages.map(img => { img.loading = 'eager'; return img.decode().catch(()=>{}); })),
      new Promise(resolve => { timeout = setTimeout(resolve, 600); })
    ]);
    clearTimeout(timeout);
    if (token === session?.token) restorePosition(position);
    send({kind:'presentationReady',request,token});
  }
  function saveAndRead() {
    if (!sticky || !session?.taskId || readOnly || savingReadOnly) return;
    if (composing) { state.textContent = '请先确认输入法候选文字'; return; }
    if (imageRequest) { state.textContent = '图片正在导入，请稍后按 Ctrl+S'; return; }
    flush(); savingReadOnly = true; applyReadState();
    send({kind:'saveReadOnly', token:session.token});
  }
  function resumeEditing() {
    if (!readOnly || savingReadOnly) return;
    readOnly = false; applyReadState();
    editor.vditor.wysiwyg.element.focus({preventScroll:true});
    send({kind:'editingResumed',token:session.token});
  }
  let outlineEnabled = false, outlineTimer = 0, outlineFrame = 0, outlineVersion = 0;
  let outlineSignature = '', outlineActive = null, outlineNodes = [], outlineNextId = 0, outlineFlashTimer = 0;
  const outlineIds = new WeakMap();
  function publishOutline(force = false) {
    clearTimeout(outlineTimer);
    if (!outlineEnabled || !ready || !session?.taskId || loading || composing) return;
    outlineNodes = [...editor.vditor.wysiwyg.element.querySelectorAll('h1,h2,h3,h4,h5,h6')]
      .filter(node => !node.closest('pre:not(.vditor-reset),.vditor-wysiwyg__block'));
    const items = outlineNodes.map(node => {
      if (!outlineIds.has(node)) outlineIds.set(node, 'heading-' + (++outlineNextId));
      return {id:outlineIds.get(node),text:node.textContent.trim() || '未命名标题',level:Number(node.tagName[1])};
    });
    const signature = JSON.stringify(items);
    if (signature !== outlineSignature) { outlineSignature = signature; outlineVersion++; force = true; }
    if (force) { send({kind:'outline',token:session.token,version:outlineVersion,items}); outlineActive = null; }
    queueOutlinePosition();
  }
  function scheduleOutline() {
    clearTimeout(outlineTimer);
    if (outlineEnabled && !composing) outlineTimer = setTimeout(publishOutline, 300);
  }
  function queueOutlinePosition() {
    if (!outlineEnabled || outlineFrame || !session || composing) return;
    outlineFrame = requestAnimationFrame(() => {
      outlineFrame = 0;
      if (!outlineEnabled || !session) return;
      const top = editor.vditor.wysiwyg.element.parentElement.getBoundingClientRect().top + 25;
      let current = outlineNodes[0];
      for (const node of outlineNodes) if (node.isConnected && node.getBoundingClientRect().top <= top) current = node;
      const id = current ? outlineIds.get(current) : null;
      if (id !== outlineActive) { outlineActive = id; send({kind:'outlineActive',token:session.token,version:outlineVersion,id}); }
    });
  }
  function enableOutline(enabled) {
    const changed = outlineEnabled !== enabled; outlineEnabled = enabled;
    const button = document.querySelector('.vditor-toolbar button[data-type="note-outline"]');
    if (button) {
      button.setAttribute('aria-expanded', String(enabled));
      button.setAttribute('aria-label', enabled ? '收起笔记目录' : '展开笔记目录');
    }
    if (enabled) { if (changed) publishOutline(true); else scheduleOutline(); }
    else { clearTimeout(outlineTimer); cancelAnimationFrame(outlineFrame); outlineFrame = 0; }
  }
  function jumpOutline(data) {
    if (!outlineEnabled || !session || data.token !== session.token || composing) return;
    publishOutline();
    if (data.version !== outlineVersion) return;
    const node = outlineNodes.find(item => outlineIds.get(item) === data.id);
    if (!node) return;
    node.scrollIntoView({block:'start',behavior:'instant'});
    clearTimeout(outlineFlashTimer);
    editor.vditor.wysiwyg.element.querySelectorAll('.island-outline-target').forEach(item=>item.classList.remove('island-outline-target'));
    node.classList.add('island-outline-target');
    outlineFlashTimer = setTimeout(()=>node.classList.remove('island-outline-target'),650);
    outlineActive = null; queueOutlinePosition();
  }
  const commands = [
    ['标题一', 'h1', '# 标题\n'], ['标题二', 'h2', '## 标题\n'], ['标题三', 'h3', '### 标题\n'],
    ['待办清单', 'todo', '- [ ] 待办事项\n'], ['无序列表', 'list', '- 列表内容\n'],
    ['有序列表', 'number', '1. 列表内容\n'], ['引用', 'quote', '> 引用内容\n'],
    ['代码块', 'code', '```text\n代码\n```\n'], ['表格', 'table', '| 项目 | 内容 |\n| --- | --- |\n| 名称 | 说明 |\n'],
    ['分割线', 'line', '\n---\n'], ['图片', 'image', '']
  ];
  function markEdited() {
    if (!session || loading || readOnly || savingReadOnly) return;
    session.dirty = true;
    queueMicrotask(() => { if (!composing) flush(); });
  }
  function flush() {
    if (!ready || !session || loading || composing || !session.dirty) return null;
    const markdown = editor.getValue();
    if (markdown === session.normalized) { session.dirty = false; return null; }
    const packet = {kind:'change', token:session.token, taskId:session.taskId,
      revision:session.revision, baseText:session.text, markdown};
    session.text = markdown; session.normalized = markdown; session.revision++; session.dirty = false;
    state.textContent = '保存中…'; send(packet); return packet;
  }
  function load(data) {
    if (!ready) { pendingLoad = data; return; }
    if (composing) { pendingLoad = data; return; }
    const position = session?.taskId === data.taskId ? readingPosition() : null;
    flush(); imageRequest = null;
    clearTimeout(outlineTimer); outlineSignature = ''; outlineActive = null; outlineNodes = [];
    if (/^\s*```(?:mermaid|math|abc|smiles|markmap|flowchart|echarts|mindmap|plantuml|graphviz|wavedrom)\b|^\s*\$\$/m.test(data.text)) {
      session = null;
      send({kind:'unsupported',text:'这份笔记含公式或图表，已转到源码编辑，原文保持完整。'});
      return;
    }
    loading = true; hideMenus();
    // Destroy pending render callbacks before changing identity; reset undo between tasks.
    clearTimeout(editor.vditor.wysiwyg.afterRenderTimeoutId);
    editor.setValue(data.text, true);
    session = {...data, normalized:editor.getValue(), dirty:false};
    writingOnly = !!data.writingOnly;
    sticky = !!data.sticky && !writingOnly; readOnly = !!data.readOnly && !writingOnly; savingReadOnly = false;
    loading = false; applyReadState();
    editor.blur();
    restorePosition(position);
    setTimeout(() => {
      if (session?.token !== data.token) return;
      // A hidden staging WebView can layout without relying on visible animation frames.
      void editor.vditor.wysiwyg.element.offsetHeight;
      send({kind:'contentReady',token:data.token});
    }, 0);
    scheduleOutline();
  }
  function place(menu, rect, above = false) {
    menu.hidden = false;
    const width = menu.offsetWidth, height = menu.offsetHeight;
    menu.style.left = Math.max(6, Math.min(rect.left, innerWidth - width - 6)) + 'px';
    const desired = above && rect.top > height + 8 ? rect.top - height - 6 : rect.bottom + 5;
    menu.style.top = Math.max(5, Math.min(desired, innerHeight - height - 5)) + 'px';
  }
  function hideMenus() { selectionTools.hidden = true; slashMenu.hidden = true; slashBlock = null; }
  function selectMenu(index) {
    slashIndex = (index + slashItems.length) % slashItems.length;
    [...slashMenu.children].forEach((button, i) => button.setAttribute('aria-selected', String(i === slashIndex)));
    slashMenu.children[slashIndex]?.scrollIntoView({block:'nearest'});
  }
  function insertCommand(index) {
    if (!slashBlock || !slashItems[index]) return;
    const range = document.createRange(); range.selectNodeContents(slashBlock);
    const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
    const command = slashItems[index];
    if (command[1] === 'image') { hideMenus(); requestImage(); return; }
    const markdown = command[2]; hideMenus(); markEdited(); editor.insertMD(markdown); flush();
  }
  function updateMenus() {
    if (!ready || loading || composing || readOnly || savingReadOnly || !session?.taskId) return hideMenus();
    const selection = getSelection();
    if (!selection?.rangeCount) return hideMenus();
    const range = selection.getRangeAt(0), root = editor.vditor.wysiwyg.element;
    if (!root.contains(range.commonAncestorContainer)) return hideMenus();
    if (!selection.isCollapsed && selection.toString().trim()) {
      slashMenu.hidden = true; selectedRange = range.cloneRange();
      place(selectionTools, range.getBoundingClientRect(), true); return;
    }
    selectionTools.hidden = true;
    const node = range.startContainer.nodeType === 1 ? range.startContainer : range.startContainer.parentElement;
    const block = node.closest('p');
    const match = block?.textContent.replace(/\u200b/g, '').match(/^\/([^\s/]*)$/);
    if (!match) { slashMenu.hidden = true; slashBlock = null; return; }
    slashItems = commands.filter(([label,key]) => label.includes(match[1]) || key.includes(match[1].toLowerCase()));
    if (!slashItems.length) { slashMenu.hidden = true; return; }
    slashBlock = block; slashMenu.replaceChildren();
    slashItems.forEach(([label,key], index) => {
      const button = document.createElement('button'); button.type = 'button'; button.setAttribute('role','option');
      const title = document.createElement('span'); title.textContent = label;
      const hint = document.createElement('small'); hint.textContent = key;
      button.append(title,hint); button.addEventListener('mousedown', event => event.preventDefault());
      button.addEventListener('click', () => insertCommand(index)); slashMenu.append(button);
    });
    place(slashMenu, range.getBoundingClientRect()); selectMenu(0);
  }
  selectionTools.addEventListener('mousedown', event => event.preventDefault());
  selectionTools.addEventListener('click', event => {
    const name = event.target.closest('button')?.dataset.command;
    if (!name || !selectedRange) return;
    const selection = getSelection(); selection.removeAllRanges(); selection.addRange(selectedRange);
    markEdited(); document.querySelector(`.vditor-toolbar button[data-type="${name}"]`)?.click();
    queueMicrotask(flush); selectionTools.hidden = true;
  });
  document.addEventListener('selectionchange', updateMenus);
  document.addEventListener('scroll', () => hideMenus(), true);
  document.addEventListener('scroll', queueOutlinePosition, true);
  document.addEventListener('load', event => { if (event.target.tagName === 'IMG') queueOutlinePosition(); }, true);
  window.addEventListener('resize', hideMenus);
  window.addEventListener('resize', queueOutlinePosition);
  window.addEventListener('blur', saveNow);
  document.addEventListener('focusout', event => {
    if (editor?.vditor.wysiwyg.element.contains(event.target)) saveNow();
  }, true);
  document.addEventListener('pointerdown', event => {
    if (outlineEnabled && !event.target.closest('button[data-type="note-outline"]')) send({kind:'outlineDismiss'});
    if (readOnly && editor.vditor.wysiwyg.element.contains(event.target) &&
        !event.target.closest('a,img,input,button') && event.button === 0) resumeEditing();
  }, true);
  for (const type of ['beforeinput','paste','cut','drop']) document.addEventListener(type, event => {
    if (readOnly || savingReadOnly) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  document.addEventListener('click', event => {
    if ((readOnly || savingReadOnly) && event.target.closest('input[type=checkbox]')) {
      event.preventDefault(); event.stopImmediatePropagation();
    }
  }, true);
  document.addEventListener('keydown', event => {
    if (writingOnly && event.ctrlKey && event.key.toLowerCase() === 's') {
      event.preventDefault(); event.stopImmediatePropagation(); saveNow(); return;
    }
    if (sticky && event.ctrlKey && event.key.toLowerCase() === 's') {
      event.preventDefault(); event.stopImmediatePropagation();
      if (!event.repeat) saveAndRead(); return;
    }
    if (composing || event.isComposing) return;
    if (event.key === 'Escape' && outlineEnabled) send({kind:'outlineDismiss'});
    if (readOnly || savingReadOnly) {
      if (savingReadOnly || ['Backspace','Delete','Enter'].includes(event.key) ||
          ((event.ctrlKey || event.metaKey) && !['c','a','f'].includes(event.key.toLowerCase()))) {
        event.preventDefault(); event.stopImmediatePropagation();
      }
      return;
    }
    if (ready && session?.taskId && event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
      const root = editor.vditor.wysiwyg.element;
      const selection = getSelection();
      // Only handle document text, not link/URL inputs or other controls.
      if (root.contains(event.target) && !event.target.closest('input,textarea,select') && selection?.rangeCount &&
          root.contains(selection.anchorNode) && root.contains(selection.focusNode)) {
        const key = event.key.toLowerCase();
        const name = {b:'bold',i:'italic',k:'link',z:'undo',y:'redo'}[key];
        const digit = /^[0-6]$/.test(key) ? Number(key) : null;
        if (name || digit !== null) {
          event.preventDefault(); event.stopImmediatePropagation(); hideMenus();
          if (event.repeat) return;
          if (name === 'undo' && imageRequest) { imageRequest = null; state.textContent = '已取消图片插入'; return; }
          const node = selection.anchorNode.nodeType === 1 ? selection.anchorNode : selection.anchorNode.parentElement;
          if (digit !== null && node.closest('pre:not(.vditor-reset),table,.vditor-wysiwyg__block')) return;
          let button;
          if (name) button = document.querySelector(`.vditor-toolbar button[data-type="${name}"]`);
          else if (digit > 0) button = document.querySelector(`.vditor-toolbar button[data-tag="h${digit}"]`);
          else if (node.closest('h1,h2,h3,h4,h5,h6')) {
            button = document.querySelector('.vditor-toolbar button[data-type="headings"]');
            // The toolbar highlight can lag behind keyboard selection changes.
            button.classList.add('vditor-menu--current');
          }
          if (button && !button.classList.contains('vditor-menu--disabled')) {
            markEdited(); button.click(); queueMicrotask(flush);
          }
          return;
        }
      }
    }
    if (!slashMenu.hidden && ['ArrowDown','ArrowUp','Enter','Escape'].includes(event.key)) {
      event.preventDefault(); event.stopImmediatePropagation();
      if (event.key === 'Escape') hideMenus();
      else if (event.key === 'Enter') insertCommand(slashIndex);
      else selectMenu(slashIndex + (event.key === 'ArrowDown' ? 1 : -1));
    }
  }, true);
  document.addEventListener('compositionstart', () => { composing = true; clearTimeout(outlineTimer); hideMenus(); }, true);
  document.addEventListener('compositionend', () => {
    composing = false; markEdited();
    scheduleOutline();
    queueMicrotask(() => {
      flush();
      if (saveAfterComposition) { saveAfterComposition = false; saveNow(); }
      if (pendingLoad) { const next = pendingLoad; pendingLoad = null; load(next); }
    });
  }, true);
  document.addEventListener('input', event => {
    if (event.target.closest('.vditor-wysiwyg')) { markEdited(); queueMicrotask(updateMenus); }
  });
  document.addEventListener('click', event => {
    if (event.target.closest('.vditor-toolbar,.vditor-wysiwyg__popover,input[type=checkbox]')) markEdited();
    const link = event.target.closest('a[href]');
    if (link) { event.preventDefault(); if (event.ctrlKey) send({kind:'link',url:link.href}); }
  });
  function requestImage(file = null) {
    if (!session?.taskId || composing || readOnly || savingReadOnly) return;
    if (imageRequest) { state.textContent = '正在导入图片，请稍候'; return; }
    const selection = getSelection(), root = editor.vditor.wysiwyg.element;
    if (!selection?.rangeCount || !root.contains(selection.getRangeAt(0).commonAncestorContainer)) editor.focus();
    if (!getSelection()?.rangeCount) return;
    if (file && (file.size > 20 * 1024 * 1024 || !['image/png','image/jpeg'].includes(file.type))) {
      state.textContent = '请选择不超过 20 MB 的 PNG 或 JPEG 图片'; return;
    }
    flush(); hideMenus();
    const pending = imageRequest = {request:crypto.randomUUID(), token:session.token, revision:session.revision,
      range:getSelection().getRangeAt(0).cloneRange()};
    state.textContent = '正在保存图片…';
    if (!file) { send({kind:'imageImport',request:pending.request,token:pending.token,choose:true}); return; }
    const reader = new FileReader();
    reader.onerror = () => { if (imageRequest === pending) { imageRequest=null; state.textContent='读取图片失败，请重试'; } };
    reader.onload = () => { if (imageRequest === pending) send({kind:'imageImport',request:pending.request,token:pending.token,base64:reader.result.split(',')[1]}); };
    reader.readAsDataURL(file);
  }
  function imageResult(data) {
    const pending=imageRequest;
    if (!pending || data.request!==pending.request || data.token!==pending.token) return;
    imageRequest=null;
    if (data.canceled) { state.textContent='已取消图片插入'; return; }
    if (data.error) { state.textContent=data.error; return; }
    const root=editor.vditor.wysiwyg.element;
    if (!session || session.token!==pending.token || session.revision!==pending.revision ||
        !root.contains(pending.range.commonAncestorContainer) || composing || readOnly || savingReadOnly) {
      state.textContent='编辑位置已变化，已取消图片插入，请重试'; return;
    }
    if (!/^attachments\/[a-f0-9]{64}\.(png|jpg)$/.test(data.reference)) return;
    editor.focus(); const selection=getSelection(); selection.removeAllRanges(); selection.addRange(pending.range);
    markEdited(); editor.insertMD(`\n![图片](${data.reference})\n`); flush();
  }
  document.addEventListener('paste', event => {
    if (!editor?.vditor.wysiwyg.element.contains(event.target)) return;
    const files = [...(event.clipboardData?.files || [])];
    if (!files.length) return;
    event.preventDefault(); event.stopImmediatePropagation();
    if (files.length!==1) { state.textContent='请一次插入一张图片'; return; }
    requestImage(files[0]);
  }, true);
  document.addEventListener('dragover', event => {
    if (event.dataTransfer?.types.includes('Files') && editor?.vditor.wysiwyg.element.contains(event.target)) event.preventDefault();
  }, true);
  document.addEventListener('drop', event => {
    if (!event.dataTransfer?.files.length) return;
    event.preventDefault(); event.stopImmediatePropagation();
    const root=editor?.vditor.wysiwyg.element;
    if (!root?.contains(event.target)) return;
    if (event.dataTransfer.files.length!==1) { state.textContent='请一次插入一张图片'; return; }
    const range=document.caretRangeFromPoint(event.clientX,event.clientY);
    if (range && root.contains(range.commonAncestorContainer)) { const selection=getSelection(); selection.removeAllRanges(); selection.addRange(range); }
    requestImage(event.dataTransfer.files[0]);
  }, true);
  document.addEventListener('dblclick',event=>{
    const img=event.target.closest('.vditor-reset img'); if (!img) return;
    const reference=img.getAttribute('src').replace(location.origin+'/','');
    if (/^attachments\/[a-f0-9]{64}\.(png|jpg)$/.test(reference)) {event.preventDefault();send({kind:'imageOpen',reference});}
  });
  document.getElementById('source').addEventListener('click', () => { imageRequest=null; flush(); send({kind:'source'}); });
  document.getElementById('finish').addEventListener('click', () => { imageRequest=null; flush(); send({kind:'preview'}); });
  window.island = {
    load, flush, readingPosition, restorePosition, preparePosition, saveAndRead, resumeEditing,
    focus:()=>{
      if(readOnly)return;
      const root=editor.vditor.wysiwyg.element;
      root.focus({preventScroll:true});
      const selection=getSelection();
      if(!selection.rangeCount || !root.contains(selection.anchorNode)) {
        const range=document.createRange();
        range.selectNodeContents(root); range.collapse(false);
        selection.removeAllRanges(); selection.addRange(range);
      }
    },
    get readOnly(){return readOnly}, get ready(){return ready}, get composing(){return composing},
    receive(data) {
      if (data.kind === 'load') load(data);
      else if (data.kind === 'outlineEnabled') enableOutline(data.enabled);
      else if (data.kind === 'outlineJump') jumpOutline(data);
      else if (data.kind === 'imageResult') imageResult(data);
      else if (data.kind === 'readOnlyResult' && data.token === session?.token) {
        savingReadOnly = false; readOnly = !!data.success; applyReadState();
        if (!data.success) state.textContent = data.error;
      }
      else if (data.kind === 'saved' && data.token === session?.token && data.revision === session?.revision && !readOnly && !savingReadOnly) state.textContent = sticky ? '已保存 · Ctrl+S 阅读' : '已保存';
      else if (data.kind === 'status') state.textContent = data.text;
    },
    // Also used by the offscreen WebView integration checks.
    get editor(){return editor}, get session(){return session}
  };
  window.chrome?.webview?.addEventListener('message', event => window.island.receive(event.data));
  editor = new Vditor('editor', {
    cdn: location.origin + '/vendor/vditor', lang:'zh_CN', mode:'wysiwyg', height:'100%', minHeight:0,
    cache:{enable:false}, undoDelay:200, placeholder:'写点什么，或输入 / 插入内容',
    toolbar:['headings','bold','italic','list','check','link','undo',
      {name:'note-outline',tip:'展开笔记目录',icon:'目录',click:()=>send({kind:'outlineToggle'})},
      {name:'more',tip:'插入与更多',toolbar:['ordered-list','quote','code','inline-code','table','line','strike','outdent','indent','redo',
        {name:'note-image',tip:'插入图片',icon:'图片',click:()=>requestImage()},
        {name:'original',tip:'导出编辑前备份',icon:'备份',click:()=>send({kind:'backup'})},
        {name:'recovery',tip:'导出冲突时保留的输入',icon:'找回',click:()=>send({kind:'recovery'})}]}],
    toolbarConfig:{pin:false}, hint:{emoji:{},extend:[]}, link:{isOpen:false}, image:{isPreview:false},
    preview:{hljs:{enable:false},markdown:{autoSpace:false,fixTermTypo:false,chinesePunct:false,toc:false,mark:false},
      theme:{current:'light'},render:{media:{enable:false}}},
    input:()=>{if(session?.dirty) flush()},
    after:()=>{
      ready=true;
      const imageObserver=new MutationObserver(()=>{
        editor.vditor.wysiwyg.element.querySelectorAll('img').forEach(img=>{img.loading='lazy';img.decoding='async';img.title='双击查看原图';});
        scheduleOutline();
      });
      imageObserver.observe(editor.vditor.wysiwyg.element,{childList:true,subtree:true,characterData:true});
      const headings = document.querySelector('.vditor-toolbar button[data-type="headings"]');
      if (headings) {
        headings.textContent = '正文⌄';
        headings.setAttribute('aria-label','标题 Ctrl+1～6；正文 Ctrl+0');
        document.querySelectorAll('.vditor-toolbar button[data-tag]').forEach(button => {
          const level = button.dataset.tag.slice(1);
          button.textContent = `${window.VditorI18n['heading'+level]}  Ctrl+${level}`;
          button.setAttribute('aria-keyshortcuts',`Control+${level}`);
        });
      }
      if(pendingLoad){const next=pendingLoad;pendingLoad=null;load(next)} send({kind:'ready'});
    }
  });
  window.addEventListener('error', event => send({kind:'error',text:event.message}));
})();
