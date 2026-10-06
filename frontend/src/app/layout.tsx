import type { Metadata } from 'next';
import './globals.css';

export const metadata: Metadata = {
  title: 'MasryVoice | منصة الوكلاء الصوتيين بالعامية المصرية',
  description: 'منصة احترافية لبناء وتشغيل وكلاء الذكاء الاصطناعي الناطقين بالعامية المصرية للحجوزات وخدمة العملاء',
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="ar" dir="rtl">
      <body>{children}</body>
    </html>
  );
}
