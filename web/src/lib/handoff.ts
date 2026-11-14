// Passes a model from one tool to another (e.g. Image -> 3D result into Prop Studio) without a round trip.

let pending: File | null = null;

export function handOff(file: File, route: 'props' | 'animator') {
  pending = file;
  window.location.hash = `#/${route}`;
}

export function takeHandOff(): File | null {
  const f = pending;
  pending = null;
  return f;
}
