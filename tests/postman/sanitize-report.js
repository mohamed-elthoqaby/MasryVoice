const fs = require('fs');
const path = require('path');

const SENSITIVE_HEADER_KEYS = new Set([
  'x-admin-key',
  'x-customer-token',
  'authorization',
  'x-test-client-id',
  'cookie',
  'set-cookie',
  'proxy-authorization'
]);

const ALLOWLISTED_HEADERS = new Set([
  'content-type',
  'content-length',
  'date',
  'server',
  'transfer-encoding',
  'connection',
  'vary',
  'access-control-allow-origin',
  'access-control-allow-methods',
  'access-control-allow-headers'
]);

// Token pattern: HMAC tokens (e.g. [Guid].[Base64/Hex Signature]) or JWTs
const TOKEN_REGEX = /[A-Za-z0-9+/=_-]{16,}\.[A-Za-z0-9+/=_-]{16,}/g;

const DEFAULT_KNOWN_SECRETS = [
  'masryvoice_admin_secret_2026',
  'masryvoice_ci_admin_key_2026',
  'masryvoice_customer_hmac_secret_key_dev_2026',
  'masryvoice_ci_customer_hmac_secret_2026',
  'masryvoice_ci_pass',
  'masryvoice_admin_secret_dev_2026',
  'masryvoice_secret_pass',
  'masryvoice_dev_password',
  'masryvoice_dev_admin_key',
  'masryvoice_dev_hmac_secret_key_12345',
  'ci_test_admin_key_super_secret_99',
  'ci_test_hmac_secret_at_least_16_chars_long'
];

/**
 * Sanitizes a Postman CLI / Newman JSON report by omitting captured bodies,
 * redacting sensitive headers, stripping stream buffer byte arrays, and verifying
 * that no runtime secrets, HMAC tokens, or customer credentials leak in the output.
 */
function sanitizeReportData(data, options = {}) {
  const envSecrets = [
    process.env.ADMIN_KEY,
    process.env.MASRYVOICE_ADMIN_KEY,
    process.env.HMAC_SECRET,
    process.env.MASRYVOICE_HMAC_SECRET
  ].filter(Boolean);
  const customSecrets = [...(options.runtimeSecrets || []), ...envSecrets];
  const allSecrets = Array.from(new Set([...DEFAULT_KNOWN_SECRETS, ...customSecrets])).filter(s => s && s.length >= 6);

  function redactString(str) {
    if (typeof str !== 'string') return str;
    let result = str;
    for (const secret of allSecrets) {
      if (result.includes(secret)) {
        result = result.split(secret).join('[REDACTED_SECRET]');
      }
    }
    result = result.replace(/"customerToken"\s*:\s*"[^"]+"/g, '"customerToken":"[REDACTED_TOKEN]"');
    result = result.replace(/"adminKey"\s*:\s*"[^"]+"/g, '"adminKey":"[REDACTED_KEY]"');
    result = result.replace(TOKEN_REGEX, '[REDACTED_TOKEN]');
    return result;
  }

  function sanitizeHeaders(headers) {
    if (!headers) return headers;
    if (Array.isArray(headers)) {
      return headers
        .filter(h => {
          const key = (h.key || h.name || '').toLowerCase();
          return ALLOWLISTED_HEADERS.has(key);
        })
        .map(h => ({
          ...h,
          value: redactString(h.value || '')
        }));
    }
    if (typeof headers === 'object') {
      const clean = {};
      for (const [k, v] of Object.entries(headers)) {
        if (ALLOWLISTED_HEADERS.has(k.toLowerCase())) {
          clean[k] = typeof v === 'string' ? redactString(v) : v;
        }
      }
      return clean;
    }
    return headers;
  }

  function cleanNode(node) {
    if (!node || typeof node !== 'object') return;

    if (Array.isArray(node)) {
      for (const item of node) {
        cleanNode(item);
      }
      return;
    }

    // Handle headers at any level
    if (node.headers) {
      node.headers = sanitizeHeaders(node.headers);
    }
    if (node.header) {
      node.header = sanitizeHeaders(node.header);
    }

    // Handle executions specifically
    if (node.executions && Array.isArray(node.executions)) {
      for (const exec of node.executions) {
        if (exec.response) {
          delete exec.response.stream;
          delete exec.response.body;
          if (exec.response.headers) {
            exec.response.headers = sanitizeHeaders(exec.response.headers);
          }
          if (exec.response.cookies) {
            exec.response.cookies = [];
          }
        }
        if (exec.request) {
          if (exec.request.body) {
            delete exec.request.body.raw;
          }
          if (exec.request.headers) exec.request.headers = sanitizeHeaders(exec.request.headers);
          if (exec.request.header) exec.request.header = sanitizeHeaders(exec.request.header);
        }
        if (exec.requestExecuted) {
          if (exec.requestExecuted.body) {
            delete exec.requestExecuted.body.raw;
          }
          if (exec.requestExecuted.headers) {
            exec.requestExecuted.headers = sanitizeHeaders(exec.requestExecuted.headers);
          }
        }
      }
    }

    // Generic traversal to strip any Buffer byte arrays or stream properties
    for (const key of Object.keys(node)) {
      const val = node[key];

      // Detect and strip Buffer representation: { type: "Buffer", data: [...] }
      if (val && typeof val === 'object' && val.type === 'Buffer' && Array.isArray(val.data)) {
        node[key] = { type: 'Buffer', data: [] };
        continue;
      }

      // Detect and strip stream property
      if (key === 'stream' && val && typeof val === 'object') {
        node[key] = { type: 'Buffer', data: [] };
        continue;
      }

      if (typeof val === 'string') {
        if (SENSITIVE_HEADER_KEYS.has(key.toLowerCase())) {
          node[key] = '[REDACTED]';
        } else {
          node[key] = redactString(val);
        }
      } else if (typeof val === 'object' && val !== null) {
        cleanNode(val);
      }
    }
  }

  // Deep clone to avoid mutating input
  const cloned = JSON.parse(JSON.stringify(data));
  cleanNode(cloned);

  // 3. Strict verification pass on serialized output
  const serialized = JSON.stringify(cloned, null, 2);
  const violations = [];

  for (const secret of allSecrets) {
    if (serialized.includes(secret)) {
      violations.push(`Unredacted secret detected in serialized report: ${secret}`);
    }
  }

  const tokenMatches = serialized.match(TOKEN_REGEX);
  if (tokenMatches && tokenMatches.length > 0) {
    violations.push(`Unredacted customer token or signature pattern detected (${tokenMatches.length} matches, sample: ${tokenMatches[0].substring(0, 10)}...)`);
  }

  // Verify decoded buffers inside output (if any remaining buffer contains secret strings)
  function inspectBuffers(obj) {
    if (!obj || typeof obj !== 'object') return;
    if (obj.type === 'Buffer' && Array.isArray(obj.data) && obj.data.length > 0) {
      const decoded = Buffer.from(obj.data).toString('utf8');
      if (TOKEN_REGEX.test(decoded)) {
        violations.push('Decoded buffer contains unredacted customer token');
      }
      for (const secret of allSecrets) {
        if (decoded.includes(secret)) {
          violations.push(`Decoded buffer contains unredacted secret: ${secret}`);
        }
      }
    }
    for (const k of Object.keys(obj)) {
      if (typeof obj[k] === 'object' && obj[k] !== null) {
        inspectBuffers(obj[k]);
      }
    }
  }
  inspectBuffers(cloned);

  if (violations.length > 0) {
    const error = new Error(`Report confidentiality verification failed with ${violations.length} violations:\n` + violations.map(v => ` - ${v}`).join('\n'));
    error.violations = violations;
    throw error;
  }

  return { sanitizedData: cloned, serializedReport: serialized };
}

function sanitizeReportFile(inputFile, outputFile, options = {}) {
  if (!fs.existsSync(inputFile)) {
    throw new Error(`Input report not found: ${inputFile}`);
  }

  const raw = fs.readFileSync(inputFile, 'utf8');
  let data;
  try {
    data = JSON.parse(raw);
  } catch (err) {
    throw new Error(`Failed to parse report JSON: ${err.message}`);
  }

  const outPath = outputFile || path.join(path.dirname(inputFile), 'sanitized', path.basename(inputFile));
  const outDir = path.dirname(outPath);
  if (!fs.existsSync(outDir)) {
    fs.mkdirSync(outDir, { recursive: true });
  }

  const { serializedReport } = sanitizeReportData(data, options);
  fs.writeFileSync(outPath, serializedReport, 'utf8');
  console.log(`[Sanitizer] Successfully sanitized report and wrote to: ${outPath}`);
  console.log('[Sanitizer] Confidentiality verification PASSED: 0 secrets or tokens detected.');
  return outPath;
}

// CLI entry point
if (require.main === module) {
  const args = process.argv.slice(2);
  if (args.length === 0) {
    console.error('Usage: node sanitize-report.js <input-report.json> [output-report.json]');
    process.exit(1);
  }

  try {
    sanitizeReportFile(args[0], args[1]);
  } catch (err) {
    console.error('====================================================');
    console.error('FATAL: Report Confidentiality Verification Failed!');
    console.error(err.message);
    console.error('====================================================');
    process.exit(1);
  }
}

module.exports = {
  sanitizeReportData,
  sanitizeReportFile,
  TOKEN_REGEX
};
