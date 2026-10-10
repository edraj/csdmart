// Draws the ```mermaid diagrams in rendered markdown (ui-shared/diagrams.ts):
//
//   <div use:diagrams={html}>{@html html}</div>
//
// The parameter is whatever the markup is rendered from. When it changes, the
// action waits for Svelte to put the new markup in the page (tick) and draws
// again; a theme change draws the figures already there in the new theme.

import { tick } from "svelte";
import { get } from "svelte/store";
import type { Action } from "svelte/action";
import { renderDiagrams } from "@shared/diagrams";
import { resolvedTheme } from "@/lib/theme";
import { _ } from "@/i18n";

export const diagrams: Action<HTMLElement, unknown> = (node) => {
  const draw = () =>
    void tick().then(() =>
      renderDiagrams(node, {
        dark: get(resolvedTheme) === "dark",
        errorLabel: get(_)("post_detail.markdown.diagram_error"),
      }),
    );
  // subscribe() calls back at once with the current theme: the first draw.
  const unsubscribe = resolvedTheme.subscribe(draw);
  return { update: draw, destroy: unsubscribe };
};
