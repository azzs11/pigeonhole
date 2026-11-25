import * as DialogPrimitive from "@radix-ui/react-dialog";
import { cn } from "@/lib/utils";

export const Dialog = DialogPrimitive.Root;
export const DialogTrigger = DialogPrimitive.Trigger;
export const DialogClose = DialogPrimitive.Close;
export const DialogTitle = ({ className, ...p }: DialogPrimitive.DialogTitleProps) => (
  <DialogPrimitive.Title className={cn("font-sign text-xl uppercase", className)} {...p} />
);
export const DialogDescription = DialogPrimitive.Description;

export const DialogContent = ({ className, children, side, ...props }: DialogPrimitive.DialogContentProps & { side?: boolean }) => (
  <DialogPrimitive.Portal>
    <DialogPrimitive.Overlay className="fixed inset-0 bg-black/40" />
    <DialogPrimitive.Content
      className={cn(
        "fixed z-50 overflow-y-auto border border-line bg-panel p-6 shadow-xl",
        side
          ? "inset-y-0 right-0 w-full max-w-xl"
          : "top-1/2 left-1/2 w-[calc(100%-32px)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg",
        className,
      )}
      {...props}
    >
      {children}
      <DialogPrimitive.Close className="absolute top-3 right-3 rounded px-2 py-1 text-sm" aria-label="Close">
        ✕
      </DialogPrimitive.Close>
    </DialogPrimitive.Content>
  </DialogPrimitive.Portal>
);
