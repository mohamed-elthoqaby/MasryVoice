const path = require('path');
const assert = require('assert');

// Resolve playwright-core from frontend node_modules
const playwrightModulePath = path.resolve(__dirname, '../../frontend/node_modules/playwright-core');
const { chromium } = require(playwrightModulePath);

const FRONTEND_URL = process.env.FRONTEND_URL || 'http://localhost:3000';
const ADMIN_KEY = process.env.ADMIN_KEY || 'masryvoice_dev_admin_key';

async function runBrowserSmokeSuite() {
  console.log('====================================================');
  console.log('   MASRYVOICE BROWSER SMOKE TEST SUITE (PLAYWRIGHT)  ');
  console.log('====================================================');
  console.log(`Target URL: ${FRONTEND_URL}`);
  console.log('Environment: Local Supported Browser (Edge/Chromium)');
  console.log('[Notice] Voice tests verify protocol orchestration and barge-in suppression using SIMULATED audio; they do not verify real acoustic STT/TTS models.\n');

  const launchOptions = {
    headless: true
  };

  if (process.env.PLAYWRIGHT_CHROMIUM_PATH) {
    launchOptions.executablePath = process.env.PLAYWRIGHT_CHROMIUM_PATH;
  } else if (process.platform === 'win32') {
    launchOptions.channel = 'msedge';
  }

  const browser = await chromium.launch(launchOptions);
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 }
  });
  const page = await context.newPage();
  page.on('console', msg => console.log('PAGE LOG:', msg.text()));
  page.on('pageerror', err => console.log('PAGE ERROR:', err.message));

  try {
    // Navigate to Dashboard
    console.log('[Step 1] Loading dashboard homepage...');
    await page.goto(FRONTEND_URL, { waitUntil: 'networkidle', timeout: 30000 });
    const title = await page.title();
    console.log(` Page title loaded: "${title}"`);
    await page.waitForSelector('#admin-auth-btn', { timeout: 10000 });
    console.log(' Dashboard navbar and controls loaded successfully.');

    // -----------------------------------------------------------------
    // TEST 1: Two-Turn Chat Flow & Staging
    // -----------------------------------------------------------------
    console.log('\n[Test 1/5] Verifying Two-Turn Chat Flow...');
    
    // Turn 1: Availability query
    console.log(' - Sending Turn 1: استفسار عن المواعيد...');
    await page.fill('#chat-input-text', 'عايز أعرف المواعيد المتاحة بكرة لكشف الباطنة');
    await page.click('#chat-send-btn');
    
    // Wait for assistant response to render
    await page.waitForFunction(() => {
      const msgs = document.querySelectorAll('.chat-bubble, [style*="border-radius: 16px"]');
      return msgs.length >= 2;
    }, { timeout: 15000 });
    console.log(' Turn 1 response received from assistant.');

    // Turn 2: Booking intent with customer details
    console.log(' - Sending Turn 2: تقديم بيانات الحجز للمعاينة...');
    await page.fill('#chat-input-text', 'احجزلي باسم محمد عاطف ورقم تليفون 01012345678');
    await page.click('#chat-send-btn');

    // Wait for pending booking preview card to appear
    await page.waitForSelector('#confirm-booking-btn', { timeout: 20000 });
    console.log(' Turn 2 complete: Pending booking card displayed with staging details.');

    // -----------------------------------------------------------------
    // TEST 2: Booking Confirmation Contract & Visual Success
    // -----------------------------------------------------------------
    console.log('\n[Test 2/5] Verifying Booking Confirmation Contract & Visual Success...');
    
    // Verify camelCase parsing fix: click confirmation button
    await page.click('#confirm-booking-btn');

    // Assert visible confirmation transition
    await page.waitForFunction(() => {
      const text = document.body.innerText;
      return text.includes('تم الحجز برقم:') || text.includes('مؤكد في السيستم') || text.includes('تم تأكيد وتثبيت الحجز بنجاح');
    }, { timeout: 15000 });

    const cardText = await page.innerText('body');
    assert.strictEqual(
      cardText.includes('تم الحجز برقم:') || cardText.includes('مؤكد في السيستم'),
      true,
      'Confirmation card must display confirmed booking ID and success status'
    );
    console.log(' Booking confirmed successfully and rendered with confirmed ID in UI.');

    // -----------------------------------------------------------------
    // TEST 3: Admin Authentication & Settings Update
    // -----------------------------------------------------------------
    console.log('\n[Test 3/5] Verifying Admin Settings & Authorization...');
    
    // Open Admin Modal
    await page.click('#admin-auth-btn');
    await page.waitForSelector('#admin-key-modal-input', { timeout: 5000 });
    await page.fill('#admin-key-modal-input', ADMIN_KEY);
    await page.click('#save-admin-key-btn');
    await page.waitForTimeout(500);

    // Navigate to Settings Tab
    await page.click('#tab-settings-btn');
    await page.waitForSelector('#agent-name-input', { timeout: 5000 });

    const newAgentName = `د. سارة الاستشارية - ${Date.now() % 1000}`;
    await page.fill('#agent-name-input', newAgentName);
    await page.click('#save-settings-btn');

    // Verify success feedback
    await page.waitForFunction(() => {
      return document.body.innerText.includes('تم حفظ إعدادات الوكيل بنجاح');
    }, { timeout: 10000 });
    console.log(` Admin settings saved successfully (Agent Name updated to "${newAgentName}").`);

    // -----------------------------------------------------------------
    // TEST 4: Initial Voice Turn & Consecutive Turn (Simulated Voice)
    // -----------------------------------------------------------------
    console.log('\n[Test 4/5] Verifying Voice Session & Consecutive Voice Turns (Simulated)...');
    
    // Switch to Voice tab
    await page.click('#tab-voice-btn');
    await page.waitForSelector('#start-voice-session-btn', { timeout: 5000 });

    // Start voice session
    await page.click('#start-voice-session-btn');
    await page.waitForSelector('#voice-barge-in-btn:not([disabled])', { timeout: 10000 });
    console.log(' Voice session established and active.');

    // Turn 1: Initial voice turn
    console.log(' - Sending Voice Turn 1: استفسار صوتي أولي...');
    await page.fill('#voice-input-text', 'مساء الخير عايز استفسار عن مواعيد العيادة');
    await page.click('#send-voice-turn-btn');

    // Wait for turn 1 completion via turn epoch display
    await page.waitForFunction(() => {
      const el = document.getElementById('voice-turn-id-display');
      return el && el.innerText.trim() === '#1';
    }, { timeout: 20000 });
    console.log(' Initial voice turn 1 completed successfully (Turn Epoch #1).');

    // Turn 2: Consecutive turn with direct token reuse
    console.log(' - Sending Voice Turn 2: استفسار صوتي ثانٍ مع التحقق من استمرارية الجلسة...');
    await page.fill('#voice-input-text', 'تمام شكراً لحضرتك');
    await page.click('#send-voice-turn-btn');

    // Wait for turn 2 completion via turn epoch display
    await page.waitForFunction(() => {
      const el = document.getElementById('voice-turn-id-display');
      return el && el.innerText.trim() === '#2';
    }, { timeout: 20000 });
    console.log(' Consecutive voice turn 2 completed successfully (Turn Epoch #2).');

    // -----------------------------------------------------------------
    // TEST 5: Barge-In Interruption & Playback Suppression
    // -----------------------------------------------------------------
    console.log('\n[Test 5/5] Verifying Barge-In Interruption & Suppression of Late Audio...');
    
    // Send voice turn and immediately trigger Barge-In interrupt
    await page.fill('#voice-input-text', 'استفسار طويل عن الخدمات المتاحة والتكلفة بالتفصيل');
    await page.click('#send-voice-turn-btn');
    
    // Immediately click barge-in
    await page.waitForTimeout(50);
    await page.click('#voice-barge-in-btn');
    console.log(' Clicked Barge-In interrupt during active turn.');

    // Verify interrupted state indicator appears and audio playback is stopped
    await page.waitForSelector('#voice-interrupted-indicator', { timeout: 15000 });
    const audioPlaying = await page.evaluate(() => {
      const audio = document.querySelector('audio');
      return audio && !audio.paused && audio.currentTime > 0;
    });
    assert.strictEqual(Boolean(audioPlaying), false, 'Audio playback must be stopped/suppressed upon Barge-In interrupt');
    console.log(' Audio playback verified suppressed upon Barge-In interrupt.');

    console.log('\n====================================================');
    console.log('ALL BROWSER SMOKE CHECKS PASSED SUCCESSFULLY (5/5)!');
    console.log('====================================================');
  } finally {
    await browser.close();
  }
}

if (require.main === module) {
  runBrowserSmokeSuite().catch(err => {
    console.error('\n❌ Browser Smoke Test Failed with error:', err);
    process.exit(1);
  });
}

module.exports = { runBrowserSmokeSuite };
