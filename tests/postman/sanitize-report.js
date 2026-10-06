const fs = require('fs');
const path = require('path');

function sanitizeReport(inputFile, outputFile) {
  if (!fs.existsSync(inputFile)) {
    console.error(`Input report not found: ${inputFile}`);
    process.exit(1);
  }

  const raw = fs.readFileSync(inputFile, 'utf8');
  let data;
  try {
    data = JSON.parse(raw);
  } catch (err) {
    console.error(`Failed to parse report JSON: ${err.message}`);
    process.exit(1);
  }

  const SENSITIVE_HEADERS = new Set([
    'x-admin-key',
    'x-customer-token',
    'authorization',
    'x-test-client-id',
    'cookie',
    'set-cookie'
  ]);

  const TOKEN_REGEX = /[A-Za-z0-9+/=]{16,}\.[A-Za-z0-9+/=]{16,}/g;
  const SECRET_STRINGS = [
    'masryvoice_admin_secret_2026',
    'masryvoice_ci_admin_key_2026',
    'masryvoice_customer_hmac_secret_key_dev_2026',
    'masryvoice_ci_customer_hmac_secret_2026',
    'masryvoice_ci_pass',
    'masryvoice_admin_secret_dev_2026'
  ];

  function redactString(str) {
    if (typeof str !== 'string') return str;
    let result = str;
    for (const secret of SECRET_STRINGS) {
      if (result.includes(secret)) {
        result = result.split(secret).join('[REDACTED_SECRET]');
      }
    }
    result = result.replace(/"customerToken"\s*:\s*"[^"]+"/g, '"customerToken":"[REDACTED_TOKEN]"');
    result = result.replace(/"adminKey"\s*:\s*"[^"]+"/g, '"adminKey":"[REDACTED_KEY]"');
    result = result.replace(TOKEN_REGEX, '[REDACTED_TOKEN]');
    return result;
  }

  function cleanObject(obj) {
    if (!obj || typeof obj !== 'object') return obj;

    if (Array.isArray(obj)) {
      for (let i = 0; i < obj.length; i++) {
        if (typeof obj[i] === 'string') {
          obj[i] = redactString(obj[i]);
        } else if (typeof obj[i] === 'object') {
          cleanObject(obj[i]);
        }
      }
      return obj;
    }

    for (const key of Object.keys(obj)) {
      // Remove encoded byte streams (raw response buffers)
      if (key === 'stream' && Array.isArray(obj[key])) {
        obj[key] = []; // Strip byte buffers
        continue;
      }

      if (key.toLowerCase() === 'customertoken' || key.toLowerCase() === 'adminkey') {
        obj[key] = '[REDACTED]';
        continue;
      }

      const val = obj[key];
      if (typeof val === 'string') {
        if (SENSITIVE_HEADERS.has(key.toLowerCase())) {
          obj[key] = '[REDACTED]';
        } else {
          obj[key] = redactString(val);
        }
      } else if (typeof val === 'object' && val !== null) {
        // Redact header arrays { key: "X-Admin-Key", value: "..." }
        if (val.key && typeof val.key === 'string' && SENSITIVE_HEADERS.has(val.key.toLowerCase())) {
          val.value = '[REDACTED]';
        }
        cleanObject(val);
      }
    }
    return obj;
  }

  // Sanitize the report data
  cleanObject(data);

  const serialized = JSON.stringify(data, null, 2);

  // STRICT CONFIDENTIALITY SCAN: Scan final serialized string for sensitive leaks
  let violations = [];
  for (const secret of SECRET_STRINGS) {
    if (serialized.includes(secret)) {
      violations.push(`Unredacted secret detected: ${secret}`);
    }
  }

  const remainingTokens = serialized.match(TOKEN_REGEX);
  if (remainingTokens && remainingTokens.length > 0) {
    violations.push(`Unredacted customer tokens/signatures detected: ${remainingTokens.length} occurrences (sample: ${remainingTokens[0].substring(0, 10)}...)`);
  }

  if (violations.length > 0) {
    console.error('====================================================');
    console.error('FATAL: Report Confidentiality Verification Failed!');
    violations.forEach(v => console.error(` - ${v}`));
    console.error('====================================================');
    process.exit(1);
  }

  const outPath = outputFile || inputFile;
  fs.writeFileSync(outPath, serialized, 'utf8');
  console.log(`Report successfully sanitized and verified. Written to: ${outPath}`);
  console.log('Artifact confidentiality scan PASSED: 0 sensitive credentials or tokens detected.');
}

const args = process.argv.slice(2);
if (args.length === 0) {
  console.error('Usage: node sanitize-report.js <input-report.json> [output-report.json]');
  process.exit(1);
}

sanitizeReport(args[0], args[1]);
