import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

export const cn = (...inputs: ClassValue[]) => twMerge(clsx(inputs));

export const num = (v: number | string | null | undefined) => (v == null ? 0 : Number(v));

/** Browser storage can throw (private windows, blocked site data); never let that break the page. */
export const storage = {
  get(key: string, area: "session" | "local" = "session") {
    try {
      return (area === "session" ? sessionStorage : localStorage).getItem(key);
    } catch {
      return null;
    }
  },
  set(key: string, value: string | null, area: "session" | "local" = "session") {
    try {
      const s = area === "session" ? sessionStorage : localStorage;
      if (value === null) s.removeItem(key);
      else s.setItem(key, value);
    } catch {
      /* ignore */
    }
  },
};
