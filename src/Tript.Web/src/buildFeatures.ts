// Build-time feature switches. Vite replaces import.meta.env values during the production build;
// training therefore cannot be enabled by changing a user's runtime environment.
export const trainingEnabled = import.meta.env.VITE_TRIPT_TRAINING === 'true';
