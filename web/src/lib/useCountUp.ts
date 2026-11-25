import { useEffect, useRef, useState } from "react";

/** Animates a number from its previous value to the new one (about 600 ms). Instant when motion is off. */
export function useCountUp(target: number, duration = 600) {
  const [shown, setShown] = useState(target);
  const from = useRef(target);
  useEffect(() => {
    const off =
      document.documentElement.getAttribute("data-motion") === "off" ||
      (typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches);
    const start = from.current;
    if (off || start === target || typeof requestAnimationFrame !== "function") {
      from.current = target;
      setShown(target);
      return;
    }
    const t0 = performance.now();
    let frame = 0;
    const step = (now: number) => {
      const p = Math.min(1, (now - t0) / duration);
      const eased = 1 - Math.pow(1 - p, 3);
      setShown(Math.round(start + (target - start) * eased));
      if (p < 1) frame = requestAnimationFrame(step);
      else from.current = target;
    };
    frame = requestAnimationFrame(step);
    return () => {
      cancelAnimationFrame(frame);
      from.current = target;
    };
  }, [target, duration]);
  return shown;
}
