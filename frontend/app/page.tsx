export default function Home() {
  return (
    <main
      style={{
        minHeight: "100dvh",
        display: "grid",
        placeItems: "center",
        padding: "2rem",
        textAlign: "center",
      }}
    >
      <div style={{ maxWidth: "42rem" }}>
        <p
          style={{
            fontFamily: "ui-monospace, monospace",
            letterSpacing: "0.14em",
            textTransform: "uppercase",
            fontSize: "0.75rem",
            color: "#0f766e",
          }}
        >
          Sales Force Automation
        </p>
        <h1 style={{ fontSize: "2.5rem", letterSpacing: "-0.02em", margin: "0.5rem 0" }}>
          FieldKit
        </h1>
        <p style={{ color: "#556169", lineHeight: 1.6 }}>
          The field app &amp; back office. Next.js is now the delivery vehicle — the UI is being
          built next (shadcn/ui, i18n, offline PWA).
        </p>
      </div>
    </main>
  );
}
