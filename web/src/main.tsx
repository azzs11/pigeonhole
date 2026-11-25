import { StrictMode, lazy, Suspense } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { KeyGate } from "@/components/KeyGate";
import { Layout } from "@/components/Layout";
import { PrefsProvider } from "@/lib/prefs";
import "./index.css";

const Overview = lazy(() => import("@/pages/Overview"));
const Messages = lazy(() => import("@/pages/Messages"));
const Approvals = lazy(() => import("@/pages/Approvals"));
const Suppressions = lazy(() => import("@/pages/Suppressions"));
const Providers = lazy(() => import("@/pages/Providers"));
const ChaosRun = lazy(() => import("@/pages/ChaosRun"));
const Settings = lazy(() => import("@/pages/Settings"));

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1 } } });

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <PrefsProvider>
        <BrowserRouter>
          <KeyGate>
            <Suspense fallback={<p className="p-6">Loading…</p>}>
              <Routes>
                <Route element={<Layout />}>
                  <Route index element={<Overview />} />
                  <Route path="messages" element={<Messages />} />
                  <Route path="approvals" element={<Approvals />} />
                  <Route path="suppressions" element={<Suppressions />} />
                  <Route path="providers" element={<Providers />} />
                  <Route path="chaos" element={<ChaosRun />} />
                  <Route path="settings" element={<Settings />} />
                </Route>
              </Routes>
            </Suspense>
          </KeyGate>
        </BrowserRouter>
      </PrefsProvider>
    </QueryClientProvider>
  </StrictMode>,
);
