import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call, type Schemas } from "./client";

const poll = { refetchInterval: 3000 } as const;

export const useSandbox = () => useQuery({ queryKey: ["sandbox"], queryFn: () => call(api.GET("/v1/sandbox")), staleTime: 60_000 });
export const useStats = () => useQuery({ queryKey: ["stats"], queryFn: () => call(api.GET("/v1/stats")), ...poll });
export const useProviders = () => useQuery({ queryKey: ["providers"], queryFn: () => call(api.GET("/v1/providers")), ...poll });
export const useMessages = () =>
  useQuery({ queryKey: ["messages"], queryFn: () => call(api.GET("/v1/messages", { params: { query: { limit: 100 } } })), ...poll });
export const useExplain = (id: string | null) =>
  useQuery({
    queryKey: ["explain", id],
    queryFn: () => call(api.GET("/v1/messages/{id}/explain", { params: { path: { id: id! } } })),
    enabled: !!id,
    ...poll,
  });
export const useMessage = (id: string) =>
  useQuery({ queryKey: ["message", id], queryFn: () => call(api.GET("/v1/messages/{id}", { params: { path: { id } } })) });
export const useApprovals = () =>
  useQuery({ queryKey: ["approvals"], queryFn: () => call(api.GET("/v1/approvals", { params: { query: { status: "Pending" } } })), ...poll });
export const useSuppressions = () => useQuery({ queryKey: ["suppressions"], queryFn: () => call(api.GET("/v1/suppressions")), ...poll });
export const usePromises = (enabled: boolean) =>
  useQuery({ queryKey: ["promises"], queryFn: () => call(api.GET("/v1/sandbox/promises")), enabled, refetchInterval: 5000 });
export const useSwitches = (enabled: boolean) =>
  useQuery({ queryKey: ["switches"], queryFn: () => call(api.GET("/v1/sandbox/switches")), enabled });

function useInvalidating<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>, keys: string[][]) {
  const qc = useQueryClient();
  return useMutation({ mutationFn: fn, onSuccess: () => keys.forEach((k) => qc.invalidateQueries({ queryKey: k })) });
}

export const useSend = () =>
  useInvalidating((body: Schemas["SendMessageRequest"]) => call(api.POST("/v1/messages", { body })), [["messages"], ["stats"]]);
export const useDecide = () =>
  useInvalidating(
    ({ id, approve, note }: { id: string; approve: boolean; note?: string }) =>
      call(
        approve
          ? api.POST("/v1/approvals/{id}/approve", { params: { path: { id } }, body: { note: note ?? null } })
          : api.POST("/v1/approvals/{id}/reject", { params: { path: { id } }, body: { note: note ?? null } }),
      ),
    [["approvals"], ["messages"], ["stats"]],
  );
export const useRemoveSuppression = () =>
  useInvalidating((address: string) => call(api.DELETE("/v1/suppressions/{address}", { params: { path: { address } } })), [["suppressions"]]);
export const useBounce = () =>
  useInvalidating(
    (id: string) => call(api.POST("/v1/sandbox/messages/{id}/bounce", { params: { path: { id } } })),
    [["messages"], ["explain"], ["suppressions"], ["stats"]],
  );
export const useBatch = () =>
  useInvalidating((count: number) => call(api.POST("/v1/sandbox/batch", { body: { count } })), [["messages"], ["stats"]]);
export const useSetSwitches = () =>
  useInvalidating((body: Schemas["SwitchesDto"]) => call(api.PUT("/v1/sandbox/switches", { body })), [["switches"], ["providers"]]);
