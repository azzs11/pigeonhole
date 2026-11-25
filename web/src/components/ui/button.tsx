import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import * as React from "react";
import { cn } from "@/lib/utils";

// shadcn/ui-style primitive, vendored and themed with the chocolate-shop tokens: square, bold, uppercase, green on hover.
const buttonVariants = cva(
  "press inline-flex items-center justify-center gap-2 font-sign text-base font-bold tracking-wide uppercase disabled:pointer-events-none disabled:opacity-50 min-h-11 px-5",
  {
    variants: {
      variant: {
        default: "bg-ink text-paper hover:bg-hover",
        outline: "border-2 border-ink bg-transparent hover:bg-ink hover:text-paper",
        good: "bg-good text-paper hover:opacity-90",
        bad: "bg-bad text-paper hover:opacity-90",
        ghost: "hover:bg-panel underline-offset-4 hover:underline",
      },
      size: { default: "", sm: "min-h-9 px-3 text-sm" },
    },
    defaultVariants: { variant: "default", size: "default" },
  },
);

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof buttonVariants> {
  asChild?: boolean;
}

export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(({ className, variant, size, asChild, ...props }, ref) => {
  const Comp = asChild ? Slot : "button";
  return <Comp ref={ref} className={cn(buttonVariants({ variant, size }), className)} {...props} />;
});
Button.displayName = "Button";
