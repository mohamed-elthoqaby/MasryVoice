const http = require('http');

const payload = JSON.stringify({
  agentId: '11111111-1111-1111-1111-111111111111',
  conversationId: '44444444-4444-4444-4444-444444444444',
  message: 'عايز أعرف إيه المواعيد المتاحة بكرة لكشف الباطنة؟'
});

const options = {
  hostname: 'localhost',
  port: 5000,
  path: '/api/chat/stream',
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(payload)
  }
};

console.log('Sending Egyptian Arabic prompt to /api/chat/stream...');
const req = http.request(options, (res) => {
  res.setEncoding('utf8');
  res.on('data', (chunk) => {
    const lines = chunk.split('\n');
    for (const line of lines) {
      if (line.startsWith('data: ')) {
        const raw = line.substring(6).trim();
        if (!raw) continue;
        try {
          const event = JSON.parse(raw);
          if (event.type === 'token') {
            process.stdout.write(event.content);
          } else if (event.type === 'tool_call') {
            console.log(`\n[⚡ TOOL CALL]: ${event.content} with args:`, event.metadata);
          } else if (event.type === 'tool_result') {
            console.log(`\n[✓ TOOL RESULT]:`, JSON.stringify(event.metadata));
          } else if (event.type === 'done') {
            console.log(`\n[DONE]: ${event.content}`);
          }
        } catch (e) {}
      }
    }
  });

  res.on('end', () => {
    console.log('\nStream completed.');
  });
});

req.on('error', (e) => {
  console.error(`Problem with request: ${e.message}`);
});

req.write(payload);
req.end();
