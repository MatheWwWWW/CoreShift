"use strict";

class BoardScene {
  constructor(canvas, select) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d");
    this.select = select;
    this.selected = 0;
    this.hovered = -1;
    this.accent = "#9bd5c8";
    this.hits = [];
    this.enabled = false;
    this.frame = 0;
    this.fan = 0;
    this.diskSlots = 1;
    this.parts = this.build(1);
    canvas.addEventListener("pointerdown", event => this.down(event));
    canvas.addEventListener("pointermove", event => this.move(event));
    canvas.addEventListener("pointerup", event => this.up(event));
    canvas.addEventListener("pointercancel", () => { this.drag = null; });
    canvas.addEventListener("pointerleave", () => this.highlight(-1));
    canvas.addEventListener("wheel", event => this.wheel(event), { passive: false });
    canvas.addEventListener("dblclick", () => this.reset());
    canvas.addEventListener("keydown", event => this.key(event));
    this.reset();
  }

  build(diskSlots) {
    const parts = [];
    const box = (x, y, z, w, h, depth, color, part = -1) => {
      const bottom = [[x,y,z],[x+w,y,z],[x+w,y+h,z],[x,y+h,z]];
      const top = bottom.map(point => [point[0], point[1], z+depth]);
      const faces = [top, [bottom[0],top[0],top[1],bottom[1]], [bottom[1],top[1],top[2],bottom[2]],
        [bottom[2],top[2],top[3],bottom[3]], [bottom[3],top[3],top[0],bottom[0]]];
      const center = [x + w/2, y + h/2, z + depth/2];
      for (const [side, vertices] of faces.entries()) parts.push({ vertices, color, shade: [1,.68,.81,.74,.59][side], part, center,
        layer: z < 0 ? -1 : 0, faceOrder: side === 0 ? 5 : side });
    };
    box(-218, -139, -12, 436, 278, 12, "#304d60");
    box(-214, -136, 0, 428, 5, 2, "#538678");
    box(-214, 131, 0, 428, 5, 2, "#538678");
    for (let i = 0; i < 5; i++) {
      box(-214, -116+i*23, 1, 32, 16, 15, "#849797");
      box(181, 9+i*18, 1, 21, 10, 12, "#425354");
    }
    for (const x of [-207, 0, 200]) for (const y of [-129, 124]) {
      box(x,y,1,9,9,2,"#bb8d56");
      box(x+2,y+2,3,5,5,1,"#384c55");
    }
    for (let i = 0; i < 11; i++) {
      const y = -77 + i * 6;
      box(-177, y, 1, 75 + 3*i, 1.4, 1, "#639b8e");
      box(137+i*3.5, 17, 1, 1.4, 80, 1, "#639b8e");
    }
    box(-92, -120, 1, 103, 93, 10, "#35454c", 0);
    box(-82, -110, 11, 83, 73, 7, "#a3b8b7", 0);
    box(-72, -101, 18, 63, 55, 4, "#4b6566", 0);
    for (let i = 0; i < 9; i++) {
      box(-84+i*10,-123,2,3,5,3,"#b89b65");
      box(-84+i*10,-27,2,3,5,3,"#b89b65");
    }
    for (const x of [35, 62]) {
      box(x, -115, 1, 19, 121, 11, "#3d5057", 2);
      box(x + 2, -109, 11, 15, 110, 8, "#649087", 2);
      for(let i=0;i<4;i++) box(x+4,-104+i*25,19,11,16,2,"#8faaac",2);
    }
    for (let i = 0; i < diskSlots; i++) {
      const y = -114 + i * 34;
      box(103, y, 2, 95, 23, 6, "#606f73", 3 + i);
      box(120, y + 4, 8, 58, 15, 5, "#84a3a1", 3 + i);
      box(188, y, 8, 9, 22, 4, "#bc9758", 3 + i);
    }
    box(-182, 42, 8, 230, 71, 19, "#35454f", 1);
    box(-174, 47, 27, 210, 60, 7, "#445e62", 1);
    box(-181, 39, 12, 7, 75, 24, "#a1b4b4", 1);
    box(-132, 109, 12, 119, 8, 4, "#bb9960", 1);
    return parts;
  }

  present(part, accent, animate, disks = 1) {
    const slots = Math.max(1, Math.min(6, disks));
    const geometryChanged = slots !== this.diskSlots;
    if (geometryChanged) {
      this.diskSlots = slots;
      this.parts = this.build(slots);
    }
    const changed = geometryChanged || !this.enabled || this.selected !== part || this.accent !== accent || this.animate !== animate;
    this.enabled = true;
    this.selected = part;
    this.accent = accent;
    this.animate = animate;
    if (changed || this.sizeChanged()) this.invalidate();
  }

  pause() {
    if (!this.enabled) return;
    this.enabled = false;
    this.hovered = -1;
    cancelAnimationFrame(this.frame);
    this.frame = 0;
    this.canvas.width = 1;
    this.canvas.height = 1;
    this.hits = [];
  }

  reset() {
    this.turn = -.25;
    this.tilt = .64;
    this.zoom = 1;
    this.invalidate();
  }

  sizeChanged() {
    return this.canvas.clientWidth !== this.w || this.canvas.clientHeight !== this.h;
  }

  invalidate() {
    if (this.enabled && !this.frame) this.frame = requestAnimationFrame(time => this.paint(time));
  }

  adjustZoom(multiplier) {
    this.zoom = Math.max(.7, Math.min(1.55, this.zoom * multiplier));
    this.invalidate();
  }

  wheel(event) {
    event.preventDefault();
    this.adjustZoom(event.deltaY < 0 ? 1.09 : .91);
  }

  key(event) {
    const rotate = { ArrowRight: .13, ArrowLeft: -.13 }[event.key];
    const tilt = { ArrowDown: .12, ArrowUp: -.12 }[event.key];
    if (rotate) this.turn += rotate;
    else if (tilt) this.tilt = Math.max(-1.04, Math.min(1.1, this.tilt + tilt));
    else if (event.key === "Home") this.reset();
    else if (event.key === "+" || event.key === "=") this.adjustZoom(1.1);
    else if (event.key === "-") this.adjustZoom(.9);
    else return;
    event.preventDefault();
    this.invalidate();
  }

  pointer(event) {
    const bounds = this.canvas.getBoundingClientRect();
    return { x: event.clientX - bounds.left, y: event.clientY - bounds.top };
  }

  down(event) {
    if (event.button !== 0) return;
    this.canvas.setPointerCapture(event.pointerId);
    const point = this.pointer(event);
    this.drag = { previous: point, origin: point, active: false };
  }

  move(event) {
    const p = this.pointer(event);
    if (!this.drag) {
      this.highlight(this.pick(p));
      return;
    }
    if (!this.drag.active && Math.hypot(p.x-this.drag.origin.x, p.y-this.drag.origin.y) < 5) return;
    this.turn += .012 * (p.x-this.drag.previous.x);
    this.tilt = Math.max(-1.04, Math.min(1.1, this.tilt + .006 * (p.y-this.drag.previous.y)));
    this.drag.previous = p;
    this.drag.active = true;
    this.highlight(-1);
    this.invalidate();
  }

  up(event) {
    if (!this.drag) return;
    const moved = this.drag.active;
    this.drag = null;
    const part = this.pick(this.pointer(event));
    if (!moved && part >= 0) this.select(part);
    this.highlight(part);
  }

  highlight(part) {
    if (this.hovered === part) return;
    this.hovered = part;
    if (part !== 1) this.fan = 0;
    this.invalidate();
  }

  pick({ x, y }) {
    for (let i = this.hits.length - 1; i >= 0; i--) {
      if (this.ctx.isPointInPath(this.hits[i].shape, x * this.pixelRatio, y * this.pixelRatio))
        return this.hits[i].part;
    }
    return -1;
  }

  rotorFaces() {
    const result = [];
    for (const x of [-128,-37]) {
      const y = 77;
      const disk = (radius, depth, color) => {
        const vertices = Array.from({length: 24}, (_, i) => {
          const angle = i * Math.PI / 12;
          return [x + radius*Math.cos(angle), y + radius*Math.sin(angle), depth];
        });
        result.push({vertices, color, shade: 1, part: 1, layer: depth});
      };
      disk(27, 35, "#809b9a");
      disk(25, 36, "#202c31");
      for (let i = 0; i < 7; i++) {
        const angle = i * 2 * Math.PI / 7 + this.fan;
        const c = Math.cos(angle);
        const s = Math.sin(angle);
        const vertices = [[4,-1],[14,-3],[21,-7],[24,-2],[20,5],[12,7],[4,3]].map(([a,b]) =>
          [x+a*c-b*s, y+a*s+b*c, 37]);
        result.push({vertices, color: "#a1b8b4", shade: 1, part: 1, layer: 37});
      }
      disk(7, 38, "#b4c4c0");
    }
    return result;
  }

  paint(time) {
    this.frame = 0;
    if (!this.enabled || this.canvas.hidden) return;
    if (this.hovered === 1 && this.animate) {
      if (time - this.lastPaint < 30) { this.invalidate(); return; }
      this.fan = time * .006;
    }
    this.lastPaint = time;
    const { canvas, ctx } = this;
    this.w = canvas.clientWidth;
    this.h = canvas.clientHeight;
    this.pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
    const w = this.w, h = this.h, pixelRatio = this.pixelRatio;
    if (!w || !h) return;
    if (Math.abs(canvas.width - w*pixelRatio) >= 1 || Math.abs(canvas.height - h*pixelRatio) >= 1) {
      canvas.width = w*pixelRatio;
      canvas.height = h*pixelRatio;
    }
    ctx.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    ctx.clearRect(0, 0, w, h);
    const scale = this.zoom * Math.min(w / 510, h / 350);
    const c = Math.cos(this.turn), s = Math.sin(this.turn), a = Math.cos(this.tilt), b = Math.sin(this.tilt);
    const project = ([x,y,z]) => {
      const v = s*x+c*y;
      return [w/2+(c*x-s*y)*scale, h/2+(a*v-b*z)*scale, b*v+a*z];
    };
    const sorted = [...this.parts, ...this.rotorFaces()].map(face => {
      const points = face.vertices.map(project);
      return { ...face, points, depth: face.center ? project(face.center)[2] : 0 };
    }).sort((first, second) => first.layer - second.layer || first.depth - second.depth || first.faceOrder - second.faceOrder);

    this.hits = [];
    for (const { points, color, shade, part } of sorted) {
      const path = new Path2D();
      path.moveTo(points[0][0], points[0][1]);
      for (const point of points.slice(1)) path.lineTo(point[0], point[1]);
      path.closePath();
      ctx.fillStyle = color;
      ctx.fill(path);
      if (shade < 1) {
        ctx.fillStyle = `rgba(9,19,21,${1-shade})`;
        ctx.fill(path);
      }
      if (part >= 0 && (part === this.selected || part === this.hovered)) {
        ctx.save();
        ctx.globalAlpha = part === this.hovered ? .31 : .21;
        ctx.fillStyle = this.accent;
        ctx.fill(path);
        ctx.restore();
        ctx.globalAlpha = .67;
        ctx.strokeStyle = this.accent;
        ctx.lineWidth = .8;
        ctx.stroke(path);
        ctx.globalAlpha = 1;
      }
      if (part >= 0) this.hits.push({ shape: path, part });
    }
    if (this.hovered === 1 && this.animate) this.invalidate();
  }
}
