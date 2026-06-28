export const metadata = {
  title: "Local Observability Demo",
  description: "Next.js + .NET + PostgreSQL observability demo"
};

export default function RootLayout({
  children
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}