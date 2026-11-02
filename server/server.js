// Servidor de sinalização mínimo, compatível com o client.js web e com o cliente WPF.
// Apenas repassa offer/answer/ice-candidate; o áudio e a tela trafegam P2P.
const http = require("http");
const { Server } = require("socket.io");

const PORT = process.env.PORT || 3000;
const server = http.createServer((req, res) => {
  res.writeHead(200, { "Content-Type": "text/plain; charset=utf-8" });
  res.end("Servidor de sinalização P2P ativo.");
});
const io = new Server(server, { cors: { origin: "*" } });

io.on("connection", (socket) => {
  let room = null;

  socket.on("join", (roomName) => {
    room = String(roomName);
    socket.join(room);
    socket.to(room).emit("user-joined", socket.id);
    console.log(`${socket.id} entrou na sala ${room}`);
  });

  socket.on("offer", (data) => io.to(data.to).emit("offer", { from: socket.id, offer: data.offer }));
  socket.on("answer", (data) => io.to(data.to).emit("answer", { from: socket.id, answer: data.answer }));
  socket.on("ice-candidate", (data) =>
    io.to(data.to || data.target).emit("ice-candidate", { from: socket.id, candidate: data.candidate }),
  );

  socket.on("disconnect", () => {
    if (room) socket.to(room).emit("user-disconnected", socket.id);
  });
});

server.listen(PORT, () => console.log(`Sinalização em http://localhost:${PORT}`));
