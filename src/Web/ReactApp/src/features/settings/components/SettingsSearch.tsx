import { Input } from '@/common/components/ui';

interface SettingsSearchProps {
  value: string;
  onChange: (value: string) => void;
}

export const SettingsSearch: React.FC<SettingsSearchProps> = ({ value, onChange }) => {
  return (
    <div className="relative w-full max-w-sm">
      <Input
        type="search"
        placeholder="Search settings..."
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-label="Search settings"
        className="pl-9"
      />
      <svg
        className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-pf-text-secondary pointer-events-none"
        xmlns="http://www.w3.org/2000/svg"
        fill="none"
        viewBox="0 0 24 24"
        strokeWidth={2}
        stroke="currentColor"
        aria-hidden="true"
      >
        <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-4.35-4.35M11 19a8 8 0 100-16 8 8 0 000 16z" />
      </svg>
    </div>
  );
};
