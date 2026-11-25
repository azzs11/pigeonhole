import type { MessageStatus } from "@/api/client";
import { cn } from "@/lib/utils";

type Tone = "good" | "warn" | "bad" | "neutral";

/** The one status palette. Every colour comes with a word; nothing is colour-only. */
export const statusLook: Record<MessageStatus, { label: string; tone: Tone }> = {
  Accepted: { label: "Accepted", tone: "neutral" },
  Sending: { label: "In transit", tone: "neutral" },
  Sent: { label: "Sent", tone: "good" },
  Delivered: { label: "Delivered", tone: "good" },
  Held: { label: "Held", tone: "warn" },
  Unknown: { label: "Unknown", tone: "warn" },
  Bounced: { label: "Returned", tone: "bad" },
  DeadLettered: { label: "Dead letter", tone: "bad" },
  Rejected: { label: "Refused", tone: "bad" },
};

export const toneClass: Record<Tone, string> = {
  good: "text-good bg-good-bg border-good",
  warn: "text-warn bg-warn-bg border-warn",
  bad: "text-bad bg-bad-bg border-bad",
  neutral: "text-neutral bg-neutral-bg border-neutral",
};

/** A rubber stamp: the only decoration in the app. */
export function StampBadge({ status, className, thunk }: { status: MessageStatus; className?: string; thunk?: boolean }) {
  const look = statusLook[status];
  return (
    <span
      data-status={status}
      className={cn(
        "inline-block -rotate-2 rounded-sm border-2 border-double px-2 py-0.5 font-sign text-xs tracking-widest uppercase",
        toneClass[look.tone],
        thunk && "thunk",
        className,
      )}
    >
      {look.label}
    </span>
  );
}
