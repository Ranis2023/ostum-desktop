const fs = require('node:fs');
const {pathToFileURL} = require('node:url');
const {chromium} = require('playwright');

async function run() {
  const [browserFile, helpFile, outputFile] = process.argv.slice(2);
  const browser = await chromium.launch({executablePath: browserFile, headless: true, args: ['--disable-background-networking']});
  try {
    const context = await browser.newContext({offline: true});
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(helpFile).href);
    const sections = await page.locator('main section').evaluateAll(items => items.map(s => s.id));
    const links = await page.locator('#links a').evaluateAll(items => items.map(a => a.hash.slice(1)));
    const results = [{id: 'H01', passed: sections.length > 10 && new Set(links).size === sections.length && sections.every(s => links.includes(s)), measure: sections.length}];
    await page.locator('#query').fill('микрофон');
    const contentHits=await page.locator('main section:visible').evaluateAll(items=>items.map(s=>s.id));
    const navHits=await page.locator('#links a:visible').evaluateAll(items=>items.map(a=>a.hash.slice(1)));
    results.push({id: 'H02', passed: contentHits.includes('voice') && contentHits.length < sections.length && contentHits.every(id => navHits.includes(id)), measure: {contentHits,navHits,count:await page.locator('#count').innerText()}});
    await page.locator('#query').fill('щщыыюю123');
    results.push({id: 'H03', passed: await page.locator('main section:visible').count() === 0 && (await page.locator('#count').innerText()).includes('Совпадений нет')});
    await page.locator('#query').fill('');
    await page.locator('#links a[href="#chat-work"]').click();
    await page.waitForURL(/#chat-work$/, {timeout: 750}).catch(()=>{});
    results.push({id: 'H04', passed: page.url().endsWith('#chat-work') && await page.locator('#chat-work').isVisible()});
    await page.evaluate(() => { window.print = () => { window.printRequested = true; }; });
    await page.locator('#print').click();
    results.push({id: 'H05', passed: await page.evaluate(() => window.printRequested === true) && errors.length === 0, errors});
    fs.writeFileSync(outputFile, JSON.stringify(results, null, 2));
  } finally {
    await browser.close();
  }
}
run().catch(error => { process.stderr.write(error.stack); process.exitCode = 1; });
