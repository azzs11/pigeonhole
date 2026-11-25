import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StampBadge } from "./StampBadge";
import { Timeline } from "./Timeline";
import { PromisesPanel } from "./PromisesPanel";
import { ApprovalCard } from "./ApprovalCard";
import { SortingOffice } from "./SortingOffice";

describe("StampBadge", () => {
  it("always pairs the colour with a word", () => {
    render(<StampBadge status="Bounced" />);
    expect(screen.getByText("Returned")).toHaveAttribute("data-status", "Bounced");
  });
});

describe("Timeline", () => {
  it("shows the summary and one plain-language step per state change", () => {
    render(
      <Timeline
        explanation={{
          status: "Unknown",
          summary: "Outcome unknown; held to avoid a duplicate.",
          steps: [
            {
              at: "2026-11-25T12:00:00Z",
              from: null,
              to: "Accepted",
              explanation: "Accepted and stored.",
            },
            {
              at: "2026-11-25T12:00:01Z",
              from: "Sending",
              to: "Unknown",
              explanation: "We cannot tell whether the provider received it.",
            },
          ],
        }}
      />,
    );
    expect(screen.getByText(/held to avoid a duplicate/)).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
    expect(screen.getByText("Unknown")).toBeInTheDocument();
  });
});

describe("PromisesPanel", () => {
  it("states a broken promise in words, not only in red", () => {
    render(
      <PromisesPanel
        promises={[
          {
            name: "Nothing sent twice",
            kept: false,
            detail: "2 Message-IDs arrived more than once.",
          },
        ]}
      />,
    );
    expect(screen.getByText("Broken: Nothing sent twice")).toBeInTheDocument();
  });
});

describe("ApprovalCard", () => {
  it("approves with A and rejects with R when the card has focus", async () => {
    const onDecide = vi.fn();
    const qc = new QueryClient({
      defaultOptions: { queries: { enabled: false } },
    });
    render(
      <QueryClientProvider client={qc}>
        <ApprovalCard
          approval={{
            id: "a1",
            messageId: "m1",
            riskScore: 50,
            reasons: ["x was not named on this run."],
            source: "guard",
          }}
          onDecide={onDecide}
        />
      </QueryClientProvider>,
    );
    const card = screen.getByRole("article");
    card.focus();
    await userEvent.keyboard("a");
    await userEvent.keyboard("r");
    expect(onDecide.mock.calls).toEqual([[true], [false]]);
    expect(screen.getByText("x was not named on this run.")).toBeInTheDocument();
  });
});

describe("SortingOffice", () => {
  it("gives screen readers the same facts as the floor plan", () => {
    render(
      <SortingOffice
        stats={{
          byStatus: { Accepted: 3, Delivered: 5 },
          outboxDepth: 2,
          acceptedLast5Min: 8,
          sentLast5Min: 5,
          quotaUsed: 8,
          dailyQuota: 100,
          pausedReason: null,
        }}
        providers={[{ name: "mailpit", circuitState: "Closed", position: 0 }]}
        down={["mailpit"]}
        motion={false}
      />,
    );
    expect(screen.getByText("Intake: 3 accepted")).toBeInTheDocument();
    expect(screen.getByText("Provider mailpit: Switched off")).toBeInTheDocument();
    expect(screen.getByText("Delivered: 5")).toBeInTheDocument();
  });
});
