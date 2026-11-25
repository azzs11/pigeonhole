import * as React from "react";
import { cn } from "@/lib/utils";

export const Input = React.forwardRef<HTMLInputElement, React.InputHTMLAttributes<HTMLInputElement>>(({ className, ...props }, ref) => (
  <input ref={ref} className={cn("min-h-11 w-full border-2 border-ink bg-panel px-3 text-sm", className)} {...props} />
));
Input.displayName = "Input";

export const Label = ({ className, ...props }: React.LabelHTMLAttributes<HTMLLabelElement>) => (
  <label className={cn("block font-sign text-sm font-bold tracking-wider uppercase", className)} {...props} />
);
