interface WebsiteConfig {
  title: string;
  footer: string;
  short_name: string;
  display_name: string;
  description: string;
  default_language: string;
  languages: Record<string, string>;
  backend: string;
  backend_timeout: number;
  delay_total_count?: boolean;
  theme?: {
    type: "solid" | "gradient";
    value: string;
  };
}

// Same-origin by default. An empty `backend` resolves to the page's origin at
// use time (see @shared/backend-url) — which is what every deployment where
// dmart serves this SPA wants, and what a hardcoded host cannot express,
// because the right answer depends on how the user reached the page.
const defaultConfig: WebsiteConfig = {
  title: "DMART Unified Data Platform",
  footer: "dmart.cc unified data platform",
  short_name: "dmart",
  display_name: "dmart",
  description: "dmart unified data platform",
  default_language: "ar",
  languages: { ar: "العربية", en: "English" },
  backend: "",
  backend_timeout: 30000,
  delay_total_count: false
};

const loadConfig = async (): Promise<WebsiteConfig> => {
  try {
    const configUrl = new URL('config.json', document.baseURI).href;
    const response = await fetch(configUrl);
    if (!response.ok) {
      throw new Error(`Failed to load config: ${response.status} ${response.statusText}`);
    }
    // Merge over the defaults rather than replacing them, matching catalog.
    // A served config.json is written once at install time and then survives
    // every upgrade, so it is always missing the keys added since — and a
    // wholesale replace turned each of those into `undefined` rather than its
    // default. That is how containerised cxb ended up with no request timeout:
    // the entrypoint's config.json has never carried backend_timeout.
    return { ...defaultConfig, ...(await response.json()) };
  } catch (error) {
    console.error('Error loading configuration:', error);
    return { ...defaultConfig };
  }
};

export let website: WebsiteConfig = { ...defaultConfig };

export const configReady: Promise<void> = loadConfig().then(config => {
  website = config;
});
