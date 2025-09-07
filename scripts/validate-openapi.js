const SwaggerParser = require('@apidevtools/swagger-parser');

async function validateOpenAPI() {
  try {
    console.log('Validating OpenAPI specification...');
    const api = await SwaggerParser.validate('./openapi/slicer-jobs.yaml');
    console.log('✅ OpenAPI specification is valid!');
    console.log(`API name: ${api.info.title}`);
    console.log(`API version: ${api.info.version}`);
    console.log(`Number of paths: ${Object.keys(api.paths).length}`);
    console.log(`Number of schemas: ${Object.keys(api.components.schemas).length}`);
    return true;
  } catch (error) {
    console.error('❌ OpenAPI validation failed:');
    console.error(error.message);
    return false;
  }
}

if (require.main === module) {
  validateOpenAPI()
    .then(success => process.exit(success ? 0 : 1))
    .catch(error => {
      console.error('Unexpected error:', error);
      process.exit(1);
    });
}

module.exports = { validateOpenAPI };