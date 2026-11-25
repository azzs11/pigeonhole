import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { storage } from "./utils";

type Prefs = { motion: boolean; setMotion: (v: boolean) => void; theme: "system" | "light" | "dark"; setTheme: (v: "system" | "light" | "dark") => void };
const Ctx = createContext<Prefs | null>(null);

/** Motion can be turned off (counters and the log remain); the OS reduced-motion setting is respected too. */
export function PrefsProvider({ children }: { children: ReactNode }) {
  const reduced = typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
  const [motion, setMotionState] = useState(() => (storage.get("pigeonhole.motion", "local") ?? (reduced ? "off" : "on")) === "on");
  const [theme, setThemeState] = useState(() => (storage.get("pigeonhole.theme", "local") as Prefs["theme"]) ?? "system");
  useEffect(() => {
    if (theme === "system") document.documentElement.removeAttribute("data-theme");
    else document.documentElement.setAttribute("data-theme", theme);
  }, [theme]);
  useEffect(() => {
    document.documentElement.setAttribute("data-motion", motion ? "on" : "off");
  }, [motion]);
  const value: Prefs = {
    motion,
    setMotion: (v) => {
      setMotionState(v);
      storage.set("pigeonhole.motion", v ? "on" : "off", "local");
    },
    theme,
    setTheme: (v) => {
      setThemeState(v);
      storage.set("pigeonhole.theme", v, "local");
    },
  };
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export const usePrefs = () => {
  const v = useContext(Ctx);
  if (!v) throw new Error("PrefsProvider missing");
  return v;
};
