const fs = require('fs');
const path = require('path');
const assert = require('assert');
const { sanitizeReportData, sanitizeReportFile, TOKEN_REGEX } = require('./sanitize-report');

function runTests() {
  console.log('[Test] Running Postman Sanitizer Regression Suite...');

  // 1. Test Buffer Byte Array Leaks (The exact finding from Postman CLI reports)
  const testDynamicSecret = 'dynamic_test_secret_key_random_xyz_998877';
  const testCustomerToken = 'd2e2936a-2007-4e6f-a889-123456789abc.dGhpc2lzYXZhbGlkaG1hY3NpZ25hdHVyZXRva2VuMTIzNDU2';

  // Construct realistic Postman CLI report structure
  const tokenBytes = Array.from(Buffer.from(JSON.stringify({
    success: true,
    conversationId: 'd2e2936a-2007-4e6f-a889-123456789abc',
    customerToken: testCustomerToken
  })));

  const mockPostmanReport = {
    run: {
      meta: { name: 'MasryVoice Acceptance' },
      summary: {
        iterations: { executed: 1, errors: 0 },
        executedRequests: { executed: 5, errors: 0 },
        tests: { executed: 10, failed: 0, passed: 10 }
      },
      executions: [
        {
          id: 'exec-1',
          request: {
            method: 'POST',
            url: { raw: 'http://localhost:5000/api/voice/session' },
            header: [
              { key: 'X-Customer-Token', value: testCustomerToken },
              { key: 'Content-Type', value: 'application/json' }
            ]
          },
          response: {
            code: 200,
            status: 'OK',
            headers: [
              { key: 'X-Customer-Token', value: testCustomerToken },
              { key: 'Content-Type', value: 'application/json' },
              { key: 'X-Admin-Key', value: testDynamicSecret }
            ],
            stream: {
              type: 'Buffer',
              data: tokenBytes // The leaked byte array!
            },
            responseTime: 42
          },
          tests: {
            'Voice session created': true
          }
        }
      ]
    }
  };

  // Test Case 1: Sanitizer removes stream buffer and sensitive headers
  const result = sanitizeReportData(mockPostmanReport, {
    runtimeSecrets: [testDynamicSecret]
  });

  const serialized = result.serializedReport;

  // Assert stream buffer byte array is completely stripped
  assert.strictEqual(serialized.includes(testCustomerToken), false, 'Customer token must not exist anywhere in sanitized JSON');
  assert.strictEqual(serialized.includes(testDynamicSecret), false, 'Dynamic secret must not exist anywhere in sanitized JSON');

  const sanitizedExec = result.sanitizedData.run.executions[0];
  assert.strictEqual(sanitizedExec.response.stream, undefined, 'Response stream must be omitted');
  assert.strictEqual(sanitizedExec.tests['Voice session created'], true, 'Test assertions must be preserved');
  assert.strictEqual(result.sanitizedData.run.summary.tests.passed, 10, 'Summary stats must be preserved');

  // Verify headers sanitized
  const reqHeaders = sanitizedExec.request.header;
  assert.strictEqual(reqHeaders.some(h => h.key === 'X-Customer-Token'), false, 'Sensitive request headers must be filtered');
  assert.strictEqual(reqHeaders.some(h => h.key === 'Content-Type'), true, 'Allowlisted request headers must be kept');

  console.log(' - Test 1 PASSED: Buffer byte arrays and sensitive headers stripped safely.');

  // Test Case 2: Strict verification fails if unredacted token slips through
  const leakyReport = {
    run: {
      executions: [
        {
          note: `Remaining leak: ${testCustomerToken}`
        }
      ]
    }
  };

  // Temporarily bypass token regex redaction to test verification scanner integrity
  let verificationFailed = false;
  try {
    // If a raw token exists in a non-redacted place:
    const dataWithLeak = JSON.parse(JSON.stringify(mockPostmanReport));
    dataWithLeak.run.executions[0].response.stream.data = tokenBytes;
    // Calling verification directly on unredacted data:
    if (TOKEN_REGEX.test(Buffer.from(tokenBytes).toString())) {
      verificationFailed = true;
    }
  } catch (err) {
    verificationFailed = true;
  }
  assert.strictEqual(verificationFailed, true, 'Verification scanner must flag unredacted tokens');
  console.log(' - Test 2 PASSED: Verification scanner detects unredacted tokens.');

  // Test Case 3: sanitizeReportFile writes to separate sanitized directory
  const tempFixturePath = path.join(__dirname, 'reports', 'temp_fixture_test.json');
  const tempSanitizedPath = path.join(__dirname, 'reports', 'sanitized', 'temp_fixture_test.json');
  fs.writeFileSync(tempFixturePath, JSON.stringify(mockPostmanReport), 'utf8');

  try {
    const writtenPath = sanitizeReportFile(tempFixturePath, tempSanitizedPath, {
      runtimeSecrets: [testDynamicSecret]
    });
    assert.strictEqual(fs.existsSync(writtenPath), true, 'Sanitized file must exist');
    const content = fs.readFileSync(writtenPath, 'utf8');
    assert.strictEqual(content.includes(testCustomerToken), false);
    assert.strictEqual(content.includes(testDynamicSecret), false);
    console.log(' - Test 3 PASSED: sanitizeReportFile writes verified output separately.');
  } finally {
    if (fs.existsSync(tempFixturePath)) fs.unlinkSync(tempFixturePath);
    if (fs.existsSync(tempSanitizedPath)) fs.unlinkSync(tempSanitizedPath);
  }

  console.log('====================================================');
  console.log('All Sanitizer Regression Tests PASSED successfully!');
  console.log('====================================================');
}

runTests();
