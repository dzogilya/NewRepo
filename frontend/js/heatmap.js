function heatColor(t) {
  const hue = 240 * (1 - Math.min(1, Math.max(0, t)));
  return `hsl(${hue}, 90%, 50%)`;
}

function drawHeatmap(canvas, map, size, min, max) {
  canvas.width = size;
  canvas.height = size;
  const ctx = canvas.getContext('2d');
  const span = max - min || 1;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      ctx.fillStyle = heatColor((map[y * size + x] - min) / span);
      ctx.fillRect(x, y, 1, 1);
    }
  }
}

function drawLegend(canvas) {
  canvas.width = 200;
  canvas.height = 1;
  const ctx = canvas.getContext('2d');
  for (let x = 0; x < 200; x++) {
    ctx.fillStyle = heatColor(x / 199);
    ctx.fillRect(x, 0, 1, 1);
  }
}
