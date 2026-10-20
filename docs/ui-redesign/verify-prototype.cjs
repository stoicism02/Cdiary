// Run with Node + Playwright. Captures ONLY the review HTML, never the WPF app.
const path = require('node:path');
const fs = require('node:fs');
const {pathToFileURL} = require('node:url');
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/cc/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.CHROME_PATH||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 const page=await browser.newPage({viewport:{width:1440,height:1050},deviceScaleFactor:1});
 const errors=[],checks=[];page.on('pageerror',e=>errors.push(e.message));
 const url=pathToFileURL(path.join(__dirname,'index.html')).href;
 const check=(condition,label)=>{if(!condition)throw new Error(label);checks.push(label);};
 fs.mkdirSync(path.join(__dirname,'concepts'),{recursive:true});
 for(const direction of ['a','b','c']){
  for(const width of [1440,760]){
   await page.setViewportSize({width,height:1050});
   for(const section of ['today','calendar','focus','search','completed','screenshots','settings']){
    await page.goto(`${url}?direction=${direction}&page=${section}`);
    check(await page.locator('#app').innerText().then(t=>t.length>50),`${direction}/${section}/${width}: rendered`);
    check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`${direction}/${section}/${width}: no page horizontal overflow`);
    if(section==='today')await page.screenshot({path:path.join(__dirname,'concepts',`${direction}-${width}-prototype.png`),fullPage:true});
   }
  }
  await page.setViewportSize({width:1440,height:1050});await page.goto(`${url}?direction=${direction}`);
  await page.locator('#sticky-demo').click();
  await page.locator('.dialog-shade img').evaluate(img=>img.decode());
  check(await page.locator('.dialog-shade img').evaluate(img=>img.naturalWidth>0),`${direction}: preserved native sticky evidence shown`);
  await page.keyboard.press('Escape');
 }
 await page.goto(url);await page.locator('[data-complete="1"]').click();
 check(await page.locator('[data-select="1"]').count()===0,'completion removes task from list');
 await page.locator('[data-page=completed]').click();check((await page.locator('#app').innerText()).includes('理解 RocketMQ'),'completion appears in archive');
 await page.locator('#undo').click();await page.locator('[data-page=today]').click();
 check(await page.locator('[data-select="1"]').count()===1,'undo restores task');
 await page.locator('[data-menu="1"]').click();await page.locator('[data-action=shelve]').click();
 check(await page.locator('[data-select="1"]').count()===0,'shelve hides active row');
 await page.locator('[data-action=resume]').click();check(await page.locator('[data-select="1"]').count()===1,'resume restores row');
 await page.locator('[data-page=search]').click();await page.locator('#search-form input').fill('RocketMQ');await page.locator('#search-form button').click();
 check(await page.locator('.search-result').count()===1,'search filters sample tasks');
 await page.locator('[data-page=focus]').click();await page.locator('[data-timer=stopwatch]').click();check(await page.locator('progress').count()===0,'stopwatch has no target progress');
 await page.locator('#evidence-toggle').click();await page.locator('#evidence-grid img').evaluateAll(imgs=>Promise.all(imgs.map(i=>{i.loading='eager';return i.decode();})));
 check(await page.locator('#evidence-grid img').evaluateAll(imgs=>imgs.length===13&&imgs.every(i=>i.naturalWidth>0)),'13 native evidence images loaded in browser');
 check(errors.length===0,'no browser JavaScript errors');
 fs.writeFileSync(path.join(__dirname,'concepts','verification-results.json'),JSON.stringify({date:'2026-10-13',method:'Headless Chrome + Playwright, HTML prototype only',checks,errors},null,2));
 await browser.close();console.log(`${checks.length} prototype checks passed; 6 home concept screenshots saved; task sticky preserved.`);
})().catch(e=>{console.error(e);process.exit(1)});
