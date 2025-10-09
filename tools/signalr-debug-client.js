const signalR = require('@microsoft/signalr');

async function main() {
  const url = process.env.SIGNALR_URL || 'http://localhost:5245/hubs/printers';
  console.log('Connecting to', url);
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(url)
    .configureLogging(signalR.LogLevel.Information)
    .build();

  connection.on('PrinterUpdated', (status) => {
    console.log('[debug-client] PrinterUpdated', JSON.stringify(status));
  });

  connection.onclose((err) => console.warn('[debug-client] closed', err));
  connection.onreconnecting((err) => console.info('[debug-client] reconnecting', err));
  connection.onreconnected((id) => console.info('[debug-client] reconnected', id));

  try {
    await connection.start();
    console.log('Connected, connectionId=', connection.connectionId);
  } catch (err) {
    console.error('Failed to connect', err);
    process.exit(1);
  }
}

main();
