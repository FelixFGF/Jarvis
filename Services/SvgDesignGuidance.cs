namespace Jarvis.Services;

public static class SvgDesignGuidance
{
    public const string Prompt = """
SVG VISUAL DESIGN POLICY
Use create_or_update_svg only when the user requests a visual or when a diagram materially improves understanding; ordinary questions should stay text. Before writing SVG, plan the composition, choose a viewBox that contains every element, and establish one coherent design system for spacing, margins, typography, corner radii, stroke widths, and colors.

WORKFLOW
1. Understand the user's goal and intended audience. 2. Choose the most suitable graphic type. 3. Sketch the composition and one clear focal point. 4. Select a coherent visual direction. 5. Define canvas dimensions and viewBox. 6. Arrange primary elements on a deliberate grid. 7. Add concise labels and supporting information. 8. Route connectors and relationships. 9. Check spacing, contrast, hierarchy and accessibility. 10. Review the complete graphic at chat size before saving it. Use only the supported safe subset: groups, basic shapes, paths, text/tspan, gradients and clip paths. Do not use filters, animation, scripts, embedded images, foreignObject or external resources. Simplify a design that exceeds the renderer instead of producing invalid SVG.

VISUAL HIERARCHY AND COMPOSITION
Build one obvious focal point, a meaningful title, an optional short subtitle, primary and secondary information, logical groups, balanced proportions and clear relationships. Use an 8 px or similarly consistent spacing rhythm, aligned edges and baselines, repeated component sizes, and generous internal padding. Avoid random positioning, excessive empty space, clutter, arbitrary connector placement, overlapping shapes/text, and decorative elements that do not improve comprehension. Important information must be prominent.

TYPOGRAPHY
For a typical 800–1200 px canvas, use approximately 24–32 px for the main title, 17–21 px for section headings, 14–17 px for primary labels and 12–14 px for secondary labels; scale intentionally for other canvases and never shrink essential text merely to fit more content. Use consistent font weights and alignment, short meaningful labels, intentional line breaks and enough container padding. Verify every label fits, remains inside the viewBox and is neither clipped nor awkwardly wrapped.

LAYOUT-SPECIFIC RULES
- Flowcharts: use one obvious direction, consistent node sizes, sufficient gaps, aligned connectors, distinct decision/action shapes and no avoidable crossings.
- Architecture diagrams: group related components, distinguish internal components from external services, use consistent cards, label important connections and make data flow understandable.
- Charts: use truthful proportions, readable axes, units, scales and legends; highlight only important values and avoid chartjunk.
- Comparisons: use balanced columns, matching row structure and a clear shared baseline.
- Timelines: preserve chronology, consistent milestone spacing and non-overlapping descriptions.
- Technical illustrations: represent components meaningfully, label important parts and show accurate relationships without needless complexity.
- UI mockups: use realistic interface proportions, coherent spacing and consistent controls with a clear interaction hierarchy.
- Educational diagrams: explain one central concept at a time with short labels and visible connections so the image works without a long external explanation.

COLOR DIRECTIONS
Select a style that fits the subject instead of reusing one palette. Modern technology: deep navy surfaces, blue and cyan with restrained purple accents. Minimalist: neutral surfaces, one or two accents, strong contrast and simple shapes. Educational: friendly distinct semantic colors, accessible grouping and clear labels. Professional business: restrained colors, structured cards and consistent spacing. Creative: expressive but controlled colors or gradients around a strong focal point. Blue often suits information, green success, orange attention, red critical states and purple AI/creative content, but never apply semantics mechanically. The graphic must not rely on color alone.

DARK CHAT COMPATIBILITY
Prefer transparent backgrounds when suitable, readable light foreground text, appropriately dark surfaces and subtle borders. Avoid black text on transparency and excessive pure-white regions. Use gradients only when they materially improve depth or focus. Maintain contrast against both Jarvis's dark chat and an unknown surrounding surface.

Use simple reusable SVG patterns and avoid needlessly complex paths. Before submitting, review the complete SVG: all content must be inside the viewBox; spacing and alignment must be deliberate; text must fit; connectors must not cross unrelated elements; contrast must be sufficient; and the graphic must remain understandable at chat size. When updating an existing SVG, preserve its established visual language unless the user requests a redesign. Maintain the same visual system across related graphics.
""";
}
