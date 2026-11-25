import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";
import { storage } from "@/lib/utils";

export type Schemas = components["schemas"];
export type MessageStatus = Schemas["MessageStatus"];

export const KEY_STORAGE = "pigeonhole.apiKey";

const auth: Middleware = {
  onRequest({ request }) {
    const key = storage.get(KEY_STORAGE);
    if (key) request.headers.set("Authorization", `Bearer ${key}`);
    return request;
  },
};

/** Typed client generated from the API's OpenAPI document (web/openapi/relay.json). */
export const api = createClient<paths>({ baseUrl: globalThis.location?.origin ?? "" });
api.use(auth);

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

/** Unwraps an openapi-fetch result, throwing the ProblemDetails title on failure. */
export async function call<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise;
  if (!response.ok) {
    const e = error as { title?: string; detail?: string } | undefined;
    throw new ApiError(response.status, e?.detail ?? e?.title ?? response.statusText);
  }
  return data as T;
}
