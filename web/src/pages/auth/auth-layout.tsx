import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Card, Group, Stack, Text, Title } from "@mantine/core";
import { ArrowUpRight, Check, Sparkles } from "lucide-react";
import { LanguageMenu } from "@/components/shell/language-menu";
import "./auth-layout.css";

interface AuthLayoutProps {
  title: string;
  subtitle: string;
  children: ReactNode;
  footer?: ReactNode;
}

/** Brand-led auth shell shared by sign-in, sign-up and password-change screens. */
export default function AuthLayout({ title, subtitle, children, footer }: AuthLayoutProps) {
  const { t } = useTranslation(["common"]);
  return (
    <main className="auth-shell">
      <section className="auth-showcase" aria-label="Biriksin">
        <div className="auth-orb auth-orb-one" />
        <div className="auth-orb auth-orb-two" />
        <div className="auth-showcase-inner">
          <BrandLockup />
          <div className="auth-showcase-copy">
            <span className="auth-eyebrow"><Sparkles size={15} /> CRM, daha akıllı</span>
            <h1>İlişkileri büyütmenin <em>en berrak</em> yolu.</h1>
            <p>Müşterileriniz, fırsatlarınız ve ekibiniz; tek bir güçlü çalışma alanında.</p>
          </div>
          <div className="auth-preview" aria-hidden="true">
            <div className="auth-preview-top"><span>Satış görünümü</span><ArrowUpRight size={18} /></div>
            <div className="auth-preview-chart">
              <i /><i /><i /><i /><i /><i /><i />
              <svg viewBox="0 0 310 105" preserveAspectRatio="none"><path d="M0 82 C30 70 35 82 60 64 S95 70 117 43 S150 58 177 33 S215 48 244 20 S280 30 310 5" /></svg>
            </div>
            <div className="auth-preview-stat"><span><b>₺1.2M</b><small>Satış potansiyeli</small></span><strong>+24.8%</strong></div>
          </div>
          <div className="auth-trust"><Check size={16} /><span>Verileriniz güvenle sizinle kalır.</span></div>
        </div>
      </section>

      <section className="auth-panel">
        <Group justify="flex-end" className="auth-language"><LanguageMenu /></Group>
        <div className="auth-form-wrap">
          <div className="auth-mobile-brand"><BrandLockup /></div>
          <Stack gap={7} className="auth-title-block">
            <Text className="auth-product-name">{t("common:app.name")}</Text>
            <Title order={2}>{title}</Title>
            <Text size="sm" c="dimmed">{subtitle}</Text>
          </Stack>
          <Card className="auth-card" padding="xl" radius="xl">
            {children}
          </Card>
          {footer && <Text className="auth-footer" size="sm" ta="center">{footer}</Text>}
        </div>
      </section>
    </main>
  );
}

function BrandLockup() {
  return (
    <div className="biriksin-logo" aria-label="Biriksin">
      <span className="biriksin-word">Biriksin</span>
      <span className="biriksin-mark"><i /><i /><b /></span>
    </div>
  );
}
