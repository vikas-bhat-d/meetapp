const appJson = require('./app.json');

module.exports = {
  ...appJson.expo,
  android: {
    ...appJson.expo.android,
    // On EAS, this is the temporary path created for the uploaded file
    // environment variable. Locally, keep using google-services.json.
    googleServicesFile:
      process.env.GOOGLE_SERVICES_JSON || appJson.expo.android.googleServicesFile,
  },
};
