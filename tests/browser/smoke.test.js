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
  console.log('[Notice] Voice tests verify protocol orchestration and barge-in suppression using SIMULATED providers;');
  console.log('         They do not establish microphone capture, real transcription, intelligible speech, or Egyptian Arabic acoustic quality.\n');

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

  const pageErrors = [];
  page.on('console', msg => console.log('PAGE LOG:', msg.text()));
  page.on('pageerror', err => {
    console.error('PAGE ERROR:', err.message);
    pageErrors.push(err.message);
  });

  function assertNoPageErrors(contextName) {
    if (pageErrors.length > 0) {
      assert.fail(`[${contextName}] Unexpected browser page error(s) occurred:\n` + pageErrors.join('\n'));
    }
  }

  try {
    // Navigate to Dashboard
    console.log('[Step 1] Loading dashboard homepage...');
    await page.goto(FRONTEND_URL, { waitUntil: 'networkidle', timeout: 30000 });
    const title = await page.title();
    console.log(` Page title loaded: "${title}"`);
    await page.waitForSelector('#admin-auth-btn', { timeout: 10000 });
    console.log(' Dashboard navbar and controls loaded successfully.');
    assertNoPageErrors('Homepage load');

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
    assertNoPageErrors('Chat Turn 1');

    // Turn 2: Booking intent with customer details
    console.log(' - Sending Turn 2: تقديم بيانات الحجز للمعاينة...');
    await page.fill('#chat-input-text', 'احجزلي باسم محمد عاطف ورقم تليفون 01012345678');
    await page.click('#chat-send-btn');

    // Wait for pending booking preview card to appear
    await page.waitForSelector('#confirm-booking-btn', { timeout: 20000 });
    console.log(' Turn 2 complete: Pending booking card displayed with staging details.');
    assertNoPageErrors('Chat Turn 2');

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
    assertNoPageErrors('Booking Confirmation');

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
    assertNoPageErrors('Admin Settings');

    // -----------------------------------------------------------------
    // TEST 4: Automatic Voice Session Creation & Consecutive Turn
    // -----------------------------------------------------------------
    console.log('\n[Test 4/5] Verifying Automatic Session Creation on First Turn & Consecutive Turn (Simulated)...');
    
    // Switch to Voice tab WITHOUT clicking start session button
    await page.click('#tab-voice-btn');
    await page.waitForSelector('#voice-input-text', { timeout: 5000 });
    console.log(' Switched to Voice tab. Session NOT explicitly started.');

    // Turn 1: First turn WITHOUT manually starting session (exercises automatic session creation in handleSendVoiceTurn)
    console.log(' - Sending Voice Turn 1 without manual session start (exercises automatic session creation)...');
    await page.fill('#voice-input-text', 'مساء الخير عايز استفسار عن مواعيد العيادة');
    await page.click('#send-voice-turn-btn');

    // Wait for turn 1 completion via turn epoch display
    await page.waitForFunction(() => {
      const el = document.getElementById('voice-turn-id-display');
      return el && el.innerText.trim() === '#1';
    }, { timeout: 25000 });
    console.log(' Voice turn 1 completed successfully with automatic session creation (Turn Epoch #1).');
    assertNoPageErrors('Automatic Session Turn 1');

    // Turn 2: Consecutive turn verifying session continuity
    console.log(' - Sending Voice Turn 2: استفسار صوتي ثانٍ مع التحقق من استمرارية الجلسة...');
    await page.waitForSelector('#voice-input-text:not([disabled])', { timeout: 15000 });
    await page.fill('#voice-input-text', 'تمام شكراً لحضرتك');
    await page.click('#send-voice-turn-btn');

    // Wait for turn 2 completion via turn epoch display
    await page.waitForFunction(() => {
      const el = document.getElementById('voice-turn-id-display');
      return el && el.innerText.trim() === '#2';
    }, { timeout: 25000 });
    console.log(' Consecutive voice turn 2 completed successfully (Turn Epoch #2).');
    assertNoPageErrors('Consecutive Turn 2');

    // -----------------------------------------------------------------
    // TEST 5: Active Playback Interruption, Delayed-Response Suppression & Recovery
    // -----------------------------------------------------------------
    console.log('\n[Test 5/5] Verifying Active Playback Barge-In, Delayed-Response Suppression & Recovery...');
    
    // Part A: Interruption of ACTIVE playback (verify playback starts before interrupt)
    console.log(' - Part A: Verifying active playback starts before barge-in interrupt...');
    await page.waitForSelector('#voice-input-text:not([disabled])', { timeout: 15000 });
    await page.fill('#voice-input-text', 'استفسار مفصل عن مواعيد وأسعار كشوفات الباطنة والأطفال');
    await page.click('#send-voice-turn-btn');

    // Wait for playback to actually start (voice-speaking-indicator appears)
    await page.waitForSelector('#voice-speaking-indicator', { timeout: 25000 });
    console.log(' Playback verified actively started (#voice-speaking-indicator is visible).');

    // Assert the actual audio element is playing before interruption
    await page.waitForFunction(() => {
      const audio = document.querySelector('audio');
      return audio && !audio.paused && audio.currentTime >= 0;
    }, { timeout: 10000 });
    const audioActuallyPlaying = await page.evaluate(() => {
      const audio = document.querySelector('audio');
      return audio && !audio.paused;
    });
    assert.strictEqual(Boolean(audioActuallyPlaying), true, 'Actual audio element must be actively playing before barge-in interrupt');
    console.log(' Verified actual audio element is actively playing before barge-in interrupt.');

    // Interrupt active playback via barge-in
    await page.click('#voice-barge-in-btn');
    console.log(' Clicked Barge-In interrupt on active playback.');

    // Verify speaking indicator disappears, interrupted indicator appears, and audio is paused
    await page.waitForSelector('#voice-interrupted-indicator', { timeout: 10000 });
    const speakingActive = await page.$('#voice-speaking-indicator');
    assert.strictEqual(speakingActive, null, 'Speaking indicator must disappear when barge-in is triggered');

    const audioPlaying = await page.evaluate(() => {
      const audio = document.querySelector('audio');
      return audio && !audio.paused && audio.currentTime > 0;
    });
    assert.strictEqual(Boolean(audioPlaying), false, 'Audio playback must be stopped/suppressed upon Barge-In interrupt');
    console.log(' Active playback verified stopped upon Barge-In interrupt.');
    assertNoPageErrors('Active Playback Interruption');

    // Part B: Deterministic delayed-response case (Simulated)
    // Route holds /api/voice/turn in flight -> user interrupts -> release delayed response -> assert cannot restart playback or overwrite state
    console.log(' - Part B: Verifying deterministic delayed-response suppression (Simulated)...');
    const turnIdBeforeDelayed = await page.$eval('#voice-turn-id-display', el => el.innerText.trim());

    let releaseDelayedResponse = null;
    const delayedResponsePromise = new Promise(resolve => {
      releaseDelayedResponse = resolve;
    });

    await page.route('**/api/voice/turn', async route => {
      console.log('   [Network Intercept] Holding /api/voice/turn in flight...');
      await delayedResponsePromise;
      console.log('   [Network Intercept] Releasing delayed /api/voice/turn response...');
      await route.continue();
    });

    // Send turn that will be delayed
    await page.waitForSelector('#voice-input-text:not([disabled])', { timeout: 15000 });
    await page.fill('#voice-input-text', 'طلب صوتي متأخر للتحقق من قمع الرد بعد المقاطعة');
    await page.click('#send-voice-turn-btn');

    // Wait 300ms for request to be in flight
    await page.waitForTimeout(300);

    // Click barge-in interrupt while request is still pending
    await page.click('#voice-barge-in-btn');
    console.log('   Triggered Barge-In while request was pending in flight.');

    // Verify interrupted state is active
    await page.waitForSelector('#voice-interrupted-indicator', { timeout: 5000 });

    // Release the delayed response
    const responsePromise = page.waitForResponse(resp => resp.url().includes('/api/voice/turn'), { timeout: 15000 });
    releaseDelayedResponse();
    await responsePromise;
    console.log('   Delayed response completed network transit.');

    // Unroute to restore normal network flow
    await page.unroute('**/api/voice/turn');

    // Wait a brief moment to ensure no late callback restarts playback
    await page.waitForTimeout(600);

    // Assert delayed response did NOT restart playback
    const speakingAfterDelayed = await page.$('#voice-speaking-indicator');
    assert.strictEqual(speakingAfterDelayed, null, 'Delayed response must not restart playback or show speaking indicator');

    const interruptedStillVisible = await page.$('#voice-interrupted-indicator');
    assert.notStrictEqual(interruptedStillVisible, null, 'Delayed response must not clear current interrupted state');

    const audioAfterDelayed = await page.evaluate(() => {
      const audio = document.querySelector('audio');
      return audio && !audio.paused && audio.currentTime > 0;
    });
    assert.strictEqual(Boolean(audioAfterDelayed), false, 'Audio playback must remain stopped after delayed response completes');

    const turnIdAfterDelayed = await page.$eval('#voice-turn-id-display', el => el.innerText.trim());
    assert.strictEqual(turnIdAfterDelayed, turnIdBeforeDelayed, 'Delayed response must not overwrite current turn ID state');

    console.log('   Delayed response safely suppressed: did not restart playback or overwrite state.');
    assertNoPageErrors('Delayed Response Suppression');

    // Part C: Verify subsequent voice turn still works cleanly (Simulated)
    console.log(' - Part C: Verifying subsequent voice turn still succeeds after interruption (Simulated)...');
    const turnDisplayBefore = await page.$eval('#voice-turn-id-display', el => el.innerText.trim());
    console.log(`   Captured turn state before new turn: ${turnDisplayBefore}`);

    // Listen for the specific new POST /api/voice/turn response
    const nextTurnResponsePromise = page.waitForResponse(
      resp => resp.url().includes('/api/voice/turn') && resp.request().method() === 'POST',
      { timeout: 25000 }
    );

    const promptMsg = 'سؤال جديد للتأكد من استئناف الخدمة بعد المقاطعة';
    await page.waitForSelector('#voice-input-text:not([disabled])', { timeout: 15000 });
    await page.fill('#voice-input-text', promptMsg);
    await page.click('#send-voice-turn-btn');

    // Await the specific new response and require success
    const nextTurnResp = await nextTurnResponsePromise;
    assert.strictEqual(nextTurnResp.ok(), true, 'Subsequent voice turn HTTP request must return HTTP 200 OK');
    const nextTurnData = await nextTurnResp.json();
    assert.strictEqual(Boolean(nextTurnData.turnId), true, 'Voice turn response must contain turnId');
    assert.strictEqual(Boolean(nextTurnData.text), true, 'Voice turn response must contain text content');

    const expectedTurnEpoch = `#${nextTurnData.turnId}`;
    console.log(`   Specific response received: turnId=${nextTurnData.turnId}, expecting UI to display ${expectedTurnEpoch}`);

    // Assert UI displays that specific response's turn ID
    await page.waitForFunction(
      (expectedEpoch) => {
        const el = document.getElementById('voice-turn-id-display');
        return el && el.innerText.trim() === expectedEpoch;
      },
      expectedTurnEpoch,
      { timeout: 25000 }
    );

    const finalTurnId = await page.$eval('#voice-turn-id-display', el => el.innerText.trim());
    assert.strictEqual(finalTurnId, expectedTurnEpoch, 'UI must display the exact turn ID from the new response');

    // Assert UI displays that specific response's content
    await page.waitForFunction(
      (expectedSnippet) => {
        const el = document.getElementById('voice-response-content-display');
        return el && el.innerText.trim().length > 0 && el.innerText.includes(expectedSnippet);
      },
      nextTurnData.text.slice(0, 15),
      { timeout: 25000 }
    );

    const finalResponseContent = await page.$eval('#voice-response-content-display', el => el.innerText.trim());
    assert.strictEqual(
      finalResponseContent.includes(nextTurnData.text.slice(0, 15)),
      true,
      'UI must display the exact response text content from the new response'
    );

    console.log('   Subsequent voice turn completed successfully and verified in UI with exact turn ID and content.');
    assertNoPageErrors('Post-Interruption Turn');

    // Verify completion before closing browser
    await page.waitForLoadState('networkidle');

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
